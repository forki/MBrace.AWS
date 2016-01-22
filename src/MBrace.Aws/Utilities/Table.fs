﻿namespace MBrace.AWS.Runtime.Utilities

open System
open System.Collections.Generic

open Nessos.FsPickler

open Amazon.DynamoDBv2
open Amazon.DynamoDBv2.DocumentModel
open Amazon.DynamoDBv2.Model

open MBrace.Core.Internals
open MBrace.AWS.Runtime

[<AllowNullLiteral>]
type IDynamoDBTableEntity =
    abstract member HashKey  : string
    abstract member RangeKey : string

[<AllowNullLiteral; AbstractClass>]
type DynamoDBTableEntity (hashKey, rangeKey) =
    member val HashKey  : string = hashKey with get
    member val RangeKey : string = rangeKey with get

    interface IDynamoDBTableEntity with
        member __.HashKey  = hashKey
        member __.RangeKey = rangeKey

[<AllowNullLiteral>]
type IDynamoDBDocument =
    abstract member ToDynamoDBDocument : unit -> Document

[<AutoOpen>]
module DynamoDBEntryExtensions =
    type DynamoDBEntry with
        static member op_Implicit (dtOffset : DateTimeOffset) : DynamoDBEntry =
            let entry = new Primitive(dtOffset.ToString())
            entry :> DynamoDBEntry

        member this.AsDateTimeOffset() =
            DateTimeOffset.Parse <| this.AsPrimitive().AsString()

[<AutoOpen>]
module private DynamoDBUtils =
    let attributeValue (x : string) = new AttributeValue(x)

[<RequireQualifiedAccess>]
module internal Table =
    type TableConfig =
        {
            ReadThroughput  : int64
            WriteThroughput : int64
            HashKey         : string
            RangeKey        : string
        }

        static member Default = 
            {
                ReadThroughput  = 10L
                WriteThroughput = 10L
                HashKey         = "HashKey"
                RangeKey        = "RangeKey"
            }

    /// Creates a new table and wait till its status is confirmed as Active
    let createIfNotExists 
            (account : AwsAccount) 
            tableName
            (tableConfig : TableConfig option)
            (maxRetries  : int option) = async {
        let tableConfig = defaultArg tableConfig TableConfig.Default
        let maxRetries  = defaultArg maxRetries 3

        let req = CreateTableRequest(TableName = tableName)
        req.KeySchema.Add(new KeySchemaElement(tableConfig.HashKey, KeyType.HASH))
        req.KeySchema.Add(new KeySchemaElement(tableConfig.RangeKey, KeyType.RANGE))
        req.ProvisionedThroughput.ReadCapacityUnits  <- tableConfig.ReadThroughput
        req.ProvisionedThroughput.WriteCapacityUnits <- tableConfig.WriteThroughput

        let create = async {
            let! ct  = Async.CancellationToken
            let! res = account.DynamoDBClient.CreateTableAsync(req, ct)
                       |> Async.AwaitTaskCorrect
                       |> Async.Catch

            match res with
            | Choice1Of2 res -> 
                return res.TableDescription.TableStatus
            | Choice2Of2 (:? ResourceInUseException) -> 
                return TableStatus.ACTIVE
            | Choice2Of2 exn -> 
                return! Async.Raise exn
        }

        let rec confirmIsActive () = async {
            let req  = DescribeTableRequest(TableName = tableName)
            let! ct  = Async.CancellationToken
            let! res = account.DynamoDBClient.DescribeTableAsync(req, ct)
                       |> Async.AwaitTaskCorrect
            if res.Table.TableStatus = TableStatus.ACTIVE
            then return ()
            else return! confirmIsActive()
        }

        let rec loop attemptsLeft = async {
            if attemptsLeft <= 0 
            then return () 
            else
                let! res = Async.Catch create
                match res with
                | Choice1Of2 status when status = TableStatus.ACTIVE -> return ()
                | Choice1Of2 _ -> do! confirmIsActive()
                | _ -> return! loop (attemptsLeft - 1)
        }

        do! loop maxRetries
    }

    let private putInternal 
            (account  : AwsAccount) 
            tableName 
            (entity   : IDynamoDBDocument)
            (opConfig : UpdateItemOperationConfig option) = async { 
        let table = Table.LoadTable(account.DynamoDBClient, tableName)
        let doc   = entity.ToDynamoDBDocument()
        let! ct   = Async.CancellationToken
            
        let update = 
            match opConfig with
            | Some config -> table.UpdateItemAsync(doc, config, ct)
            | _           -> table.UpdateItemAsync(doc, ct)

        do! update
            |> Async.AwaitTaskCorrect
            |> Async.Ignore
    }

    let put account tableName entity =
        putInternal account tableName entity None

    let putBatch 
            (account : AwsAccount) 
            tableName 
            (entities : 'a seq when 'a :> IDynamoDBDocument) = async {
        let table = Table.LoadTable(account.DynamoDBClient, tableName)
        let batch = table.CreateBatchWrite()
        let docs  = entities |> Seq.map (fun x -> x.ToDynamoDBDocument()) 
        docs |> Seq.iter batch.AddDocumentToPut
        let! ct   = Async.CancellationToken

        do! batch.ExecuteAsync(ct)
            |> Async.AwaitTaskCorrect
    }

    let delete 
            (account : AwsAccount) 
            tableName 
            (entity : IDynamoDBDocument) = async {
        let table = Table.LoadTable(account.DynamoDBClient, tableName)
        let! ct   = Async.CancellationToken
        do! table.DeleteItemAsync(entity.ToDynamoDBDocument(), ct)
            |> Async.AwaitTaskCorrect
            |> Async.Ignore
    }

    let deleteBatch
            (account : AwsAccount) 
            tableName 
            (entities : 'a seq when 'a :> IDynamoDBDocument) = async {
        let table = Table.LoadTable(account.DynamoDBClient, tableName)
        let batch = table.CreateBatchWrite()
        let docs  = entities |> Seq.map (fun x -> x.ToDynamoDBDocument()) 
        docs |> Seq.iter batch.AddItemToDelete
        let! ct   = Async.CancellationToken

        do! batch.ExecuteAsync(ct)
            |> Async.AwaitTaskCorrect
    }

    let inline query< ^a when ^a : (static member FromDynamoDBDocument : Document -> ^a) > 
            (account : AwsAccount) 
            tableName 
            (hashKey : string) = async {
        let results = ResizeArray<_>()

        let rec loop lastKey =
            async {
                let req = QueryRequest(TableName = tableName)
                let eqCond = new Condition()
                eqCond.ComparisonOperator <- ComparisonOperator.EQ
                eqCond.AttributeValueList.Add(new AttributeValue(hashKey))
                req.KeyConditions.Add("HashKey", eqCond)
                req.ExclusiveStartKey <- lastKey

                let! ct  = Async.CancellationToken
                let! res = account.DynamoDBClient.QueryAsync(req, ct)
                           |> Async.AwaitTaskCorrect

                res.Items 
                |> Seq.map Document.FromAttributeMap 
                |> Seq.map (fun d -> (^a : (static member FromDynamoDBDocument : Document -> ^a) d))
                |> results.AddRange

                if res.LastEvaluatedKey.Count > 0 then
                    do! loop res.LastEvaluatedKey
            }
        do! loop (Dictionary<string, AttributeValue>())

        return results :> ICollection<_>
    }

    let private readInternal 
            (account : AwsAccount) 
            tableName 
            (hashKey : string) 
            (rangeKey : string) = async {
        let req = GetItemRequest(TableName = tableName)
        req.Key.Add("HashKey",  new AttributeValue(hashKey))
        req.Key.Add("RangeKey", new AttributeValue(rangeKey))

        let! ct  = Async.CancellationToken
        let! res = account.DynamoDBClient.GetItemAsync(req, ct)
                   |> Async.AwaitTaskCorrect
        return res
    }

    let inline read< ^a when ^a : (static member FromDynamoDBDocument : Document -> ^a) > 
            (account : AwsAccount) 
            tableName 
            (hashKey : string) 
            (rangeKey : string) = async {
        let! res = readInternal account tableName hashKey rangeKey

        return Document.FromAttributeMap res.Item 
               |> (fun d -> (^a : (static member FromDynamoDBDocument : Document -> ^a) d))
    }

    let inline increment 
            (account : AwsAccount) 
            tableName 
            hashKey
            rangeKey
            fieldToIncr = async {
        // see http://docs.aws.amazon.com/amazondynamodb/latest/developerguide/Expressions.Modifying.html#Expressions.Modifying.UpdateExpressions
        let req  = UpdateItemRequest(TableName = tableName)
        req.Key.Add("HashKey",  attributeValue hashKey)
        req.Key.Add("RangeKey", attributeValue rangeKey)
        req.ExpressionAttributeNames.Add("#F", fieldToIncr)
        req.ExpressionAttributeValues.Add(":val", attributeValue "1")
        req.UpdateExpression <- "SET #F = #F+:val"
        req.ReturnValues <- ReturnValue.UPDATED_NEW

        let! ct  = Async.CancellationToken
        let! res = account.DynamoDBClient.UpdateItemAsync(req, ct)
                    |> Async.AwaitTaskCorrect
        
        let newValue = res.Attributes.[fieldToIncr]
        return System.Int64.Parse newValue.N
    }

    let inline transact< ^a when ^a : (static member FromDynamoDBDocument : Document -> ^a)
                            and  ^a :> IDynamoDBDocument >
            (account  : AwsAccount) 
            tableName 
            (hashKey  : string) 
            (rangeKey : string)
            (conditionalField : ^a -> string * string option) // used to perform conditional update
            (update   : ^a -> ^a) = async {
        let read () = async {
            let! res = readInternal account tableName hashKey rangeKey
            return Document.FromAttributeMap res.Item 
                   |> (fun d -> (^a : (static member FromDynamoDBDocument : Document -> ^a) d))
        }

        let rec transact x = async {
            let x' = update x
            
            let config = UpdateItemOperationConfig()
            let (condField, condFieldVal) = conditionalField x
            match condFieldVal with
            | Some x ->
                config.Expected.Add(condField, DynamoDBEntry.op_Implicit x)
            | _ -> 
                config.ConditionalExpression.ExpressionAttributeNames.Add("#F", condField)
                config.ConditionalExpression.ExpressionStatement <- "attribute_not_exists(#F)"

            let! res = Async.Catch <| putInternal account tableName x' (Some config)
            match res with
            | Choice1Of2 _ -> return x'
            | Choice2Of2 (:? ConditionalCheckFailedException) ->
                let! x = read()
                return! transact x
            | Choice2Of2 exn -> return raise exn
        }
        
        let! x = read()
        return! transact x
    }

    let inline readStringOrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName then doc.[fieldName].AsString() else null

    let inline readIntOrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName then nullable <| doc.[fieldName].AsInt() else nullableDefault<int>
    
    let inline readInt64OrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName then nullable <| doc.[fieldName].AsLong() else nullableDefault<int64>
    
    let inline readBoolOrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName then nullable <| doc.[fieldName].AsBoolean() else nullableDefault<bool>

    let inline readDateTimeOffsetOrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName 
        then nullable <| doc.[fieldName].AsDateTimeOffset() 
        else nullableDefault<DateTimeOffset>

    let inline readByteArrayOrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName then doc.[fieldName].AsByteArray() else null

    let inline readDoubleOrDefault (doc : Document) fieldName =
        if doc.ContainsKey fieldName 
        then nullable <| doc.[fieldName].AsDouble() 
        else nullableDefault<double>