﻿namespace MBrace.AWS.Runtime.Utilities

open System
open System.Net
open System.IO
open System.Threading
open System.Threading.Tasks
open System.Text.RegularExpressions

open MBrace.Core.Internals
open MBrace.Runtime.Utils
open MBrace.Runtime.Utils.Retry
open MBrace.AWS.Runtime

open Amazon.S3
open Amazon.S3.Model

[<AutoOpen>]
module S3Utils =

    let private s3Regex = new Regex("^\s*/([^/]*)/?(.*)", RegexOptions.Compiled)
    let private s3UriRegex = new Regex("^\s*s3://([^/]+)/?(.*)", RegexOptions.Compiled)
    let private s3ArnRegex = new Regex("^\s*arn:aws:s3:::([^/]+)/?(.*)", RegexOptions.Compiled)
    let private directoryNameRegex = new Regex("^(.*)[^/]+$", RegexOptions.Compiled ||| RegexOptions.RightToLeft)
    let private fileNameRegex = new Regex("[^/]*$", RegexOptions.Compiled)
    let private lastFolderRegex = new Regex("([^/]*)/[^/]*$", RegexOptions.Compiled)

    type S3Path = { Bucket : string ; Key : string }
    with
        member p.FullPath = 
            if String.IsNullOrEmpty p.Key then "/" + p.Bucket
            else
                sprintf "/%s/%s" p.Bucket p.Key

        member p.IsBucket = String.IsNullOrEmpty p.Key
        member p.IsRoot = String.IsNullOrEmpty p.Bucket
        member p.IsObject = not(p.IsRoot || p.IsBucket)

        static member TryParse (path : string, ?forceKeyNameGuidelines : bool, ?asDirectory : bool) =
            let forceKeyNameGuidelines = defaultArg forceKeyNameGuidelines false
            let asDirectory = defaultArg asDirectory false
            let inline extractResult (m : Match) =
                let bucketName = m.Groups.[1].Value
                let keyName = m.Groups.[2].Value
                if not (String.IsNullOrEmpty bucketName || Validate.tryBucketName bucketName) then None
                elif not (String.IsNullOrEmpty keyName || Validate.tryKeyName forceKeyNameGuidelines keyName) then None
                else
                    Some { Bucket = bucketName ; Key = if asDirectory && keyName <> "" && not <| keyName.EndsWith "/" then keyName + "/" else keyName  }
                
            let m = s3Regex.Match path
            if m.Success then extractResult m else
            
            let m = s3UriRegex.Match path
            if m.Success then extractResult m else

            let m = s3ArnRegex.Match path
            if m.Success then extractResult m else

            None

        static member Parse(path : string, ?forceKeyNameGuidelines : bool, ?asDirectory : bool) : S3Path =
            let forceKeyNameGuidelines = defaultArg forceKeyNameGuidelines false
            let asDirectory = defaultArg asDirectory false
            let inline extractResult (m : Match) =
                let bucketName = m.Groups.[1].Value
                let keyName = m.Groups.[2].Value
                if not <| String.IsNullOrEmpty bucketName then Validate.bucketName bucketName
                if not <| String.IsNullOrEmpty keyName then Validate.keyName forceKeyNameGuidelines keyName
                { Bucket = bucketName ; Key = if asDirectory && keyName <> "" && not <| keyName.EndsWith "/" then keyName + "/" else keyName }

            let m = s3Regex.Match path
            if m.Success then extractResult m else
            
            let m = s3UriRegex.Match path
            if m.Success then extractResult m else

            let m = s3ArnRegex.Match path
            if m.Success then extractResult m else

            invalidArg "path" <| sprintf "Invalid S3 path format '%s'." path

        static member Combine([<ParamArray>]paths : string []) = 
            let acc = new ResizeArray<string>()
            for path in paths do
                if path.StartsWith "/" || path.StartsWith "s3://" || path.StartsWith "arn:aws:s3:::" then acc.Clear()
                elif acc.Count > 0 && not <| acc.[acc.Count - 1].EndsWith "/" then acc.Add "/"
                acc.Add path

            String.concat "" acc

        static member Normalize(path : string) = S3Path.Parse(path).FullPath

        static member GetDirectoryName(path : string) = 
            let m = directoryNameRegex.Match path
            if m.Success then m.Groups.[1].Value
            elif path.Contains "/" then path
            else ""

        static member GetFileName(path : string) =
            let m = fileNameRegex.Match path
            if m.Success then m.Groups.[0].Value else ""

        static member GetFolderName(path : string) =
            let m = lastFolderRegex.Match path
            if m.Success then m.Groups.[1].Value else ""


    [<Sealed; AutoSerializable(false)>]
    type S3WriteStream internal (client : IAmazonS3, bucketName : string, key : string, uploadId : string, timeout : TimeSpan option) =
        inherit Stream()

        static let bufSize = 5 * 1024 * 1024 // 5 MiB : the minimum upload size per non-terminal chunk permited by Amazon
        static let bufPool = System.ServiceModel.Channels.BufferManager.CreateBufferManager(256L, bufSize)

        let cts = new CancellationTokenSource()
        let mutable position = 0L
        let mutable i = 0
        let mutable buffer = bufPool.TakeBuffer bufSize
        let mutable etag : string option = None
        let uploads = new ResizeArray<Task<UploadPartResponse>>()

        let mutable isClosed = 0
        let acquireClose() = Interlocked.CompareExchange(&isClosed, 1, 0) = 0
        let checkClosed() = if isClosed = 1 then raise <| new ObjectDisposedException("S3WriteStream")

        let upload releaseBuf (bytes : byte []) (offset : int) (count : int) =
            let request = new UploadPartRequest(
                                BucketName = bucketName, 
                                Key = key, 
                                UploadId = uploadId, 
                                PartNumber = uploads.Count + 1, 
                                InputStream = new MemoryStream(bytes, offset, count))

            let task = client.UploadPartAsync(request, cts.Token)
            if releaseBuf then
                ignore <| task.ContinueWith(fun (_ : Task) -> bufPool.ReturnBuffer bytes)

            uploads.Add(task)

        let flush isFinalFlush =
            if i > 0 then
                upload true buffer 0 i
                if not isFinalFlush then 
                    buffer <- bufPool.TakeBuffer bufSize
                    i <- 0

        let close () = async {
            if not <| acquireClose() then () else

            flush true
            if uploads.Count = 0 then upload false buffer 0 0 // part uploads require at least one chunk
            let! results = uploads |> Task.WhenAll |> Async.AwaitTaskCorrect
            let partETags = results |> Seq.map (fun r -> new PartETag(r.PartNumber, r.ETag))
            let request = 
                new CompleteMultipartUploadRequest(
                    BucketName = bucketName,
                    Key = key,
                    UploadId = uploadId,
                    PartETags = new ResizeArray<_>(partETags))

            let! response = Async.AwaitTaskCorrect <| client.CompleteMultipartUploadAsync(request, cts.Token)
            etag <- Some response.ETag
            return ()
        }

        let abort () =
            if acquireClose() then 
                client.AbortMultipartUploadAsync(bucketName, key, uploadId) |> ignore

        do 
            match timeout with
            | None -> ()
            | Some t ->
                let _ = cts.Token.Register(fun () -> abort())
                cts.CancelAfter t

        override __.CanRead    = false
        override __.CanSeek    = false
        override __.CanWrite   = true
        override __.CanTimeout = true

        override __.Length = position
        override __.Position 
            with get () = position
            and  set _  = raise <| NotSupportedException()

        override __.SetLength _ = raise <| NotSupportedException()
        override __.Seek (_, _) = raise <| NotSupportedException()
        override __.Read (_, _, _) = raise <| NotSupportedException()

        override __.Write (source : byte [], offset : int, count : int) =
            checkClosed()
            if offset < 0 || count < 0 || offset + count > source.Length then raise <| ArgumentOutOfRangeException()

            let mutable offset = offset
            let mutable count = count

            while i + count >= bufSize do
                let k = bufSize - i
                Buffer.BlockCopy(source, offset, buffer, i, k)
                i <- bufSize
                offset <- offset + k
                count <- count - k
                position <- position + int64 k
                flush false

            if count > 0 then
                Buffer.BlockCopy(source, offset, buffer, i, count)
                position <- position + int64 count
                i <- i + count
            
        override __.Flush() = ()
        override __.Close() = Async.RunSync(close(), cancellationToken = cts.Token)

        /// Abort object write operation
        member __.Abort() = checkClosed() ; abort ()
        /// Gets the etag of the writen object; must only be called after Close() has completed
        member __.ETag = Option.get etag
        member __.CloseAsync() = close()

    [<Sealed; AutoSerializable(false)>]
    type S3SeekableReadStream internal (client : IAmazonS3, bucketName : string, key : string, length : int64, stream : Stream, timeout : TimeSpan option, etag : string) =
        inherit Stream()

        let mutable stream = stream
        let mutable position = 0L

        let mutable isClosed = 0
        let acquireClose() = Interlocked.CompareExchange(&isClosed, 1, 0) = 0 
        let checkClosed() = if isClosed = 1 then raise <| new ObjectDisposedException("S3SeekableReadStream")

        static member internal GetRangedStream (client : IAmazonS3) bucket key (index : (int64 * int64) option) (etag : string option) (timeout : TimeSpan option) = async {
            let req = new GetObjectRequest(BucketName = bucket, Key = key)
            index |> Option.iter (fun (s,e) -> req.ByteRange <- new ByteRange(s,e))
            etag |> Option.iter (fun e -> req.EtagToMatch <- e)
            timeout |> Option.iter (fun t -> req.ResponseExpires <- DateTime.Now + t)

            let! ct = Async.CancellationToken
            let! res = client.GetObjectAsync(req, ct) |> Async.AwaitTaskCorrect
            return res
        }
        
        override __.CanRead    = true
        override __.CanSeek    = true
        override __.CanWrite   = false
        override __.CanTimeout = true

        override __.Length = length
        override __.Position 
            with get () = position
            and  set i  = __.Seek(i, SeekOrigin.Begin) |> ignore

        override __.SetLength _ = raise <| NotSupportedException()
        override __.Seek (i : int64, origin : SeekOrigin) =
            checkClosed()
            let start = 
                match origin with 
                | SeekOrigin.Begin -> i
                | SeekOrigin.Current -> position + i
                | _ -> invalidArg "origin" "not supported SeekOrigin.End"

            stream.Close()
            let response = S3SeekableReadStream.GetRangedStream client bucketName key (Some(start, length)) (Some etag) timeout |> Async.RunSync
            stream <- response.ResponseStream
            position <- start
            start

        override __.Read (buf, off, cnt) = 
            let read = stream.Read(buf, off, cnt) 
            position <- position + int64 read 
            read

        override __.Write (_, _, _) = raise <| NotSupportedException()
            
        override __.Flush() = ()
        override __.Close() = if acquireClose() then stream.Close()


    let private mkBucketRetryPolicy maxRetries interval =
        let interval = defaultArg interval 3000 |> float |> TimeSpan.FromMilliseconds
        Policy(fun retries exn -> 
            if maxRetries |> Option.exists (fun mr -> retries > mr) then None else
            match exn with
            | :? AmazonS3Exception as e when e.StatusCode = HttpStatusCode.Conflict -> Some interval
            | :? AmazonS3Exception as e when e.StatusCode = HttpStatusCode.NotFound && e.Message.Contains "bucket" -> Some interval
            | _ -> None)

    type IAmazonS3 with

        /// <summary>
        ///     Asynchronously gets an object write stream for given uri in S3 storage
        /// </summary>
        /// <param name="bucketName"></param>
        /// <param name="key"></param>
        /// <param name="timeout"></param>
        member s3.GetObjectWriteStreamAsync(bucketName : string, key : string, ?timeout : TimeSpan) : Async<S3WriteStream> = async {
            let! ct = Async.CancellationToken
            let request = new InitiateMultipartUploadRequest(BucketName = bucketName, Key = key)
            let! response = s3.InitiateMultipartUploadAsync(request, ct) |> Async.AwaitTaskCorrect
            return new S3WriteStream(s3, bucketName, key, response.UploadId, timeout)
        }

        /// <summary>
        ///     Asynchronously gets a seekable read stream for given uri in S3 storage
        /// </summary>
        /// <param name="bucketName"></param>
        /// <param name="key"></param>
        /// <param name="timeout"></param>
        /// <param name="etag"></param>
        member s3.GetObjectSeekableReadStreamAsync(bucketName : string, key : string, ?timeout : TimeSpan, ?etag : string) : Async<S3SeekableReadStream> = async {
            let! response = S3SeekableReadStream.GetRangedStream s3 bucketName key None etag timeout
            return new S3SeekableReadStream(s3, bucketName, key, response.ContentLength, response.ResponseStream, timeout, response.ETag)
        }

        /// Asynchronously deletes an S3 bucket even if it may be populated with objects
        member s3.DeleteBucketAsyncSafe(bucketName : string) = async {
            let! ct = Async.CancellationToken
            let! response = 
                s3.ListObjectsAsync(bucketName, cancellationToken = ct) 
                |> Async.AwaitTaskCorrect
                |> Async.Catch

            match response with
            | Choice1Of2 objects ->
                if objects.S3Objects.Count > 0 then
                    let request = new DeleteObjectsRequest(BucketName = bucketName)
                    for o in objects.S3Objects do
                        request.Objects.Add(new KeyVersion(Key = o.Key))

                    let! _response = s3.DeleteObjectsAsync(request, ct) |> Async.AwaitTaskCorrect
                    do! Async.Sleep 1000
                    do! s3.DeleteBucketAsyncSafe(bucketName)
                else
                    let! _ = s3.DeleteBucketAsync(bucketName, ct) |> Async.AwaitTaskCorrect
                    return ()

            | Choice2Of2 e when StoreException.NotFound e -> ()
            | Choice2Of2 e -> do! Async.Raise e
        }

        /// CreatesIfNotExistAsync that protects from 409 conflict errors with supplied retry policy
        member s3.CreateBucketIfNotExistsSafe(bucketName : string, ?retryInterval : int, ?maxRetries : int) = async {
            let policy = mkBucketRetryPolicy maxRetries retryInterval

            do! retryAsync policy <| async {
                let! ct = Async.CancellationToken
                let! listed = s3.ListBucketsAsync(ct) |> Async.AwaitTaskCorrect
                let buckOpt = listed.Buckets |> Seq.tryFind (fun b -> b.BucketName = bucketName)
                if Option.isNone buckOpt then
                    let! ct = Async.CancellationToken
                    let! _result = s3.PutBucketAsync(bucketName, ct) |> Async.AwaitTaskCorrect
                    ()

                if buckOpt |> Option.forall (fun b -> (DateTime.UtcNow - b.CreationDate).Duration() < TimeSpan.FromMinutes 1.) then
                    // addresses an issue where S3 erroneously reports that bucket does not exist even if it has been created
                    // the workflow below will typically trigger this error, forcing a retry of the operation after a delay
                    let! ct = Async.CancellationToken
                    let! r1 = s3.InitiateMultipartUploadAsync(InitiateMultipartUploadRequest(BucketName = bucketName, Key = Guid.NewGuid().ToString("N")), ct) |> Async.AwaitTaskCorrect
                    let! _r2 = s3.UploadPartAsync(UploadPartRequest(BucketName = r1.BucketName, Key = r1.Key, PartNumber = 1, UploadId = r1.UploadId, InputStream = new MemoryStream([||])), ct) |> Async.AwaitTaskCorrect
                    let! _r3 = s3.AbortMultipartUploadAsync(AbortMultipartUploadRequest(BucketName = r1.BucketName, Key = r1.Key, UploadId = r1.UploadId), ct) |> Async.AwaitTaskCorrect
    //                let! _r3 = account.S3Client.CompleteMultipartUploadAsync(CompleteMultipartUploadRequest(BucketName = r1.BucketName, Key = r1.Key, UploadId = r1.UploadId, PartETags = ResizeArray [new PartETag(1, _r2.ETag)]), ct) |> Async.AwaitTaskCorrect
                    ()
            }
        }