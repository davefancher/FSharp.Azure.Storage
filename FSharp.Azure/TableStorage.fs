﻿namespace DigitallyCreated.FSharp.Azure

    open System;

    module TableStorage =

        open System.Collections.Generic
        open System.Linq
        open System.Threading.Tasks
        open Microsoft.FSharp.Linq.QuotationEvaluation
        open Microsoft.FSharp.Quotations
        open Microsoft.FSharp.Quotations.Patterns
        open Microsoft.FSharp.Reflection
        open Microsoft.WindowsAzure.Storage
        open Microsoft.WindowsAzure.Storage.Table
        open Utilities

        let MaxBatchSize = 100

        type EntityIdentifier = { PartitionKey : string; RowKey : string; }
        type OperationResult = { HttpStatusCode : int; Etag : string }
        type EntityMetadata = { Etag : string; Timestamp : DateTimeOffset }

        type IEntityIdentifiable = 
            abstract member GetIdentifier : unit -> EntityIdentifier

        [<AllowNullLiteralAttribute>]
        type PartitionKeyAttribute () = inherit Attribute()
        
        [<AllowNullLiteralAttribute>]
        type RowKeyAttribute () = inherit Attribute()

        type Operation<'T> =
            | Insert of entity : 'T
            | InsertOrMerge of entity : 'T
            | InsertOrReplace of entity : 'T
            | Replace of entity : 'T * etag : string
            | ForceReplace of entity : 'T
            | Merge of entity : 'T * etag : string
            | ForceMerge of entity : 'T
            member this.GetEntity() = 
                match this with
                | Insert (entity) -> entity
                | InsertOrMerge (entity) -> entity
                | InsertOrReplace (entity) -> entity
                | Merge (entity, _) -> entity
                | ForceMerge (entity) -> entity
                | Replace (entity, _) -> entity
                | ForceReplace (entity) -> entity

        [<AbstractClass; Sealed>]
        type EntityIdentiferReader<'T> private () =
            static let buildIdentiferFromAttributesFunc() =
                let partitionKeyProperty = getPropertyByAttribute<'T, PartitionKeyAttribute, string>()
                let rowKeyProperty = getPropertyByAttribute<'T, RowKeyAttribute, string>()

                let var = Var("o", typeof<'T>)
                let pk = Expr.PropertyGet (Expr.Var(var), partitionKeyProperty)
                let rk = Expr.PropertyGet (Expr.Var(var), rowKeyProperty)
            
                let recordInitializer = <@ { PartitionKey = %%pk; RowKey = %%rk } @>

                let quotation = Expr.Cast<'T -> EntityIdentifier>(Expr.Lambda(var, recordInitializer))
                quotation.Compile()()

            static let defaultGetIdentifier = 
                match typeof<'T> with
                | t when typeof<IEntityIdentifiable>.IsAssignableFrom t -> fun (e : 'T) -> (box e :?> IEntityIdentifiable).GetIdentifier()
                | t when typeof<ITableEntity>.IsAssignableFrom t -> fun (e : 'T) -> 
                    let tableEntity = (box e :?> ITableEntity)
                    { PartitionKey = tableEntity.PartitionKey; RowKey = tableEntity.RowKey }
                | _ -> buildIdentiferFromAttributesFunc()
                    
            static member GetIdentifier = ref defaultGetIdentifier


        type private RecordTableEntityWrapper<'T>(record : 'T, identifier, etag) =
            static let recordFields = 
                FSharpType.GetRecordFields typeof<'T>
            static let recordReader = 
                FSharpValue.PreComputeRecordReader typeof<'T>
            static let recordWriter = 
                FSharpValue.PreComputeRecordConstructor typeof<'T> >> (fun o -> o :?> 'T)

            static member ResolveRecord (pk : string) (rk : string) (timestamp: DateTimeOffset) (properties : IDictionary<string, EntityProperty>) (etag : string) =
                let propValues = 
                    recordFields 
                        |> Seq.map (fun f -> 
                            match properties |> tryGet f.Name with
                            | Some prop -> 
                                match prop.PropertyAsObject with
                                | null -> runtimeGetUncheckedDefault f.PropertyType
                                | value when value.GetType() <> f.PropertyType -> 
                                    failwithf "The property %s on type %s of type %s has deserialized as the incorrect type %s" f.Name typeof<'T>.Name f.PropertyType.Name (value.GetType().Name)
                                | value -> value
                            | None -> 
                                match f.Name with
                                | "PartitionKey" when f.PropertyType = typeof<string> -> pk :> obj
                                | "RowKey" when f.PropertyType = typeof<string> -> rk :> obj
                                | "Timestamp" when f.PropertyType = typeof<DateTimeOffset> -> timestamp :> obj
                                | _ -> runtimeGetUncheckedDefault f.PropertyType)
                        |> Seq.toArray
                (recordWriter propValues), { Etag = etag; Timestamp = timestamp }

            member this.Record = record

            interface ITableEntity with
                member val PartitionKey : string = identifier.PartitionKey with get, set
                member val RowKey : string = identifier.RowKey with get, set
                member val ETag : string = etag with get, set
                member val Timestamp : DateTimeOffset = Unchecked.defaultof<_> with get, set

                member this.ReadEntity(properties, operationContext) =
                    notImplemented()

                member this.WriteEntity(operationContext) = 
                    record 
                        |> recordReader
                        |> Seq.map EntityProperty.CreateEntityPropertyFromObject 
                        |> Seq.zip (recordFields |> Seq.map (fun p -> p.Name))
                        |> Seq.filter (fun (name, _) -> name <> "PartitionKey" && name <> "RowKey")
                        |> dict

        [<AbstractClass; Sealed>]
        type private EntityTypeCache<'T> private () = 
            static let tableEntityTypeConstructor = lazy(
                let ctor = typeof<'T>.GetConstructor([||])
                if ctor = null then failwithf "Type %s does not have a parameterless constructor" typeof<'T>.Name
                let lambda = Expr.Lambda(Var("unit", typeof<unit>), Expr.NewObject(ctor, []))
                Expr.Cast<unit -> 'T>(lambda).Compile()())

            static let resolveTableEntity (pk : string) (rk : string) (timestamp: DateTimeOffset) (properties : IDictionary<string, EntityProperty>) (etag : string) =
                let entity = tableEntityTypeConstructor.Value()
                let tableEntity = box entity :?> ITableEntity
                do tableEntity.PartitionKey <- pk
                do tableEntity.RowKey <- rk
                do tableEntity.Timestamp <- timestamp
                do tableEntity.ReadEntity(properties, null)
                do tableEntity.ETag <- etag
                entity, { Etag = etag; Timestamp = timestamp }

            static let createTableOperationFromTableEntity (tableOperation : ITableEntity -> TableOperation) (entity : 'T) etag =
                let entity = box entity :?> ITableEntity
                entity.ETag <- etag
                entity |> tableOperation

            static let createTableOperationFromRecord (tableOperation : ITableEntity -> TableOperation) (record : 'T) etag =
                let eId = !(EntityIdentiferReader.GetIdentifier) record
                RecordTableEntityWrapper (record, eId, etag) |> tableOperation

            static member val Resolver = lazy (
                match typeof<'T> with
                | t when typeof<ITableEntity>.IsAssignableFrom t -> resolveTableEntity
                | t when FSharpType.IsRecord t -> RecordTableEntityWrapper.ResolveRecord
                | t -> failwithf "Type %s must be either an ITableEntity or an F# record type" t.Name)

            static member val CreateTableOperation = lazy (
                match typeof<'T> with
                    | t when typeof<ITableEntity>.IsAssignableFrom t -> createTableOperationFromTableEntity
                    | t when FSharpType.IsRecord t -> createTableOperationFromRecord
                    | _ -> failwithf "Type %s must be either an ITableEntity or an F# record type" typeof<'T>.Name)

        type EntityQuery<'T> = 
            { Filter : string
              TakeCount : int option }
            static member get_Zero() : EntityQuery<'T> = 
                { Filter = ""; TakeCount = None }
            static member (+) (left : EntityQuery<'T>, right : EntityQuery<'T>) =
                let filter =
                    match left.Filter, right.Filter with
                    | "", "" -> ""
                    | l, "" -> l
                    | "", r -> r
                    | l, r -> TableQuery.CombineFilters (left.Filter, "and", right.Filter)
                let takeCount = 
                    match left.TakeCount, right.TakeCount with
                    | Some l, Some r -> Some (min l r)
                    | Some l, None -> Some l
                    | None, Some r -> Some r
                    | None, None -> None

                { Filter = filter; TakeCount = takeCount }

            member this.ToTableQuery() = 
                TableQuery (FilterString = this.Filter, TakeCount = (this.TakeCount |> toNullable))
                

        module Query = 
            open DerivedPatterns

            type SystemProperties =
                { PartitionKey : string
                  RowKey : string
                  Timestamp : DateTimeOffset }

            type private Comparison =
                | Equals
                | GreaterThan
                | GreaterThanOrEqual
                | LessThan
                | LessThanOrEqual
                | NotEqual
                member this.CommutativeInvert() = 
                    match this with
                    | GreaterThan -> LessThan
                    | GreaterThanOrEqual -> LessThanOrEqual
                    | LessThan -> GreaterThan
                    | LessThanOrEqual -> GreaterThanOrEqual
                    | Equals -> Equals
                    | NotEqual -> NotEqual
            
            let private toOperator comparison = 
                match comparison with
                | Equals -> QueryComparisons.Equal
                | GreaterThan -> QueryComparisons.GreaterThan
                | GreaterThanOrEqual -> QueryComparisons.GreaterThanOrEqual
                | LessThan -> QueryComparisons.LessThan
                | LessThanOrEqual -> QueryComparisons.LessThanOrEqual
                | NotEqual -> QueryComparisons.NotEqual

            let private notFilter filter =
                sprintf "not (%s)" filter

            let private (|ComparisonOp|_|) (expr : Expr) =
                match expr with
                | SpecificCall <@ (=) @> (_, _, [left; right]) -> Some (ComparisonOp Equals, left, right)
                | SpecificCall <@ (>) @> (_, _, [left; right]) -> Some (ComparisonOp GreaterThan, left, right)
                | SpecificCall <@ (>=) @> (_, _, [left; right]) -> Some (ComparisonOp GreaterThanOrEqual, left, right)
                | SpecificCall <@ (<) @> (_, _, [left; right]) -> Some (ComparisonOp LessThan, left, right)
                | SpecificCall <@ (<=) @> (_, _, [left; right]) -> Some (ComparisonOp LessThanOrEqual, left, right)
                | SpecificCall <@ (<>) @> (_, _, [left; right]) -> Some (ComparisonOp NotEqual, left, right)
                | _ -> None

            let private (|PropertyComparison|_|) (expr : Expr) =
                match expr with
                | ComparisonOp (op, PropertyGet (Some (Var(v)), prop, []), valExpr) -> 
                    Some (PropertyComparison (v, prop, op, valExpr))
                | ComparisonOp (op, valExpr, PropertyGet (Some (Var(v)), prop, [])) -> 
                    Some (PropertyComparison (v, prop, op.CommutativeInvert(), valExpr))
                | PropertyGet (Some (Var(v)), prop, []) when prop.PropertyType = typeof<bool> -> 
                    Some (PropertyComparison (v, prop, Equals, Expr.Value(true)))
                | SpecificCall <@ not @> (None, _, [ PropertyGet (Some (Var(v)), prop, []) ]) when prop.PropertyType = typeof<bool> -> 
                    Some (PropertyComparison (v, prop, Equals, Expr.Value(false)))
                | _ -> None

            let private (|ComparisonValue|_|) (expr : Expr) =
                match expr with
                | Value (o, t) -> Some(ComparisonValue (o))
                | expr when expr.GetFreeVars().Any() -> failwithf "Cannot evaluate %A to a comparison value as it contains free variables" expr
                | expr -> Some(ComparisonValue (expr.EvalUntyped()))

            let private generateFilterCondition type' propertyName op (value : obj) = 
               match type' with
                | t when t = typeof<string> -> TableQuery.GenerateFilterCondition (propertyName, op |> toOperator, value :?> string)
                | t when t = typeof<byte[]> -> TableQuery.GenerateFilterConditionForBinary (propertyName, op |> toOperator, value :?> byte[])
                | t when t = typeof<bool> -> TableQuery.GenerateFilterConditionForBool (propertyName, op |> toOperator, value :?> bool)
                | t when t = typeof<DateTimeOffset> -> TableQuery.GenerateFilterConditionForDate (propertyName, op |> toOperator, value :?> DateTimeOffset)
                | t when t = typeof<double> -> TableQuery.GenerateFilterConditionForDouble (propertyName, op |> toOperator, value :?> double)
                | t when t = typeof<Guid> -> TableQuery.GenerateFilterConditionForGuid (propertyName, op |> toOperator, value :?> Guid)
                | t when t = typeof<int> -> TableQuery.GenerateFilterConditionForInt (propertyName, op |> toOperator, value :?> int)
                | t when t = typeof<int64> -> TableQuery.GenerateFilterConditionForLong (propertyName, op |> toOperator, value :?> int64)
                | t -> failwithf "Unexpected property type %s for property %s" t.Name propertyName

            let private isPropertyComparisonAgainstBool expr =
                match expr with
                | PropertyComparison (_, prop, _, _) when prop.PropertyType = typeof<bool> -> true
                | _ -> false

            let private buildPropertyFilter entityVar sysPropVar expr =
                let rec buildPropertyFilterRec expr = 
                    match expr with
                    | AndAlso (left, right) -> 
                        TableQuery.CombineFilters(buildPropertyFilterRec left, "and", buildPropertyFilterRec right)
                    | OrElse (left, right) -> 
                        TableQuery.CombineFilters(buildPropertyFilterRec left, "or", buildPropertyFilterRec right)
                    | SpecificCall <@ not @> (None, _, [nottedExpr]) when not (nottedExpr |> isPropertyComparisonAgainstBool) -> 
                        notFilter (buildPropertyFilterRec nottedExpr)
                    | PropertyComparison (v, prop, op, ComparisonValue (value)) ->
                        if v <> entityVar && v <> sysPropVar then
                            failwithf "Comparison (%A) to property (%s) on value that is not the function parameter (%s)" op prop.Name v.Name
                        generateFilterCondition prop.PropertyType prop.Name op value
                    | _ -> failwithf "Unable to understand expression: %A" expr
                buildPropertyFilterRec expr

            let private makePropertyFilter (expr : Expr<'T -> SystemProperties -> bool>) =
                if expr.GetFreeVars().Any() then
                    failwithf "The expression %A contains free variables." expr
                match expr with
                | Lambda (entityVar, Lambda (sysPropVar, expr)) -> buildPropertyFilter entityVar sysPropVar expr
                | _ -> failwith "Unexpected expression; lambda not found"


            let all<'T> : EntityQuery<'T> = EntityQuery.get_Zero()

            let where (expr : Expr<'T -> SystemProperties -> bool>) (query : EntityQuery<'T>) = 
                [query; { Filter = expr |> makePropertyFilter; TakeCount = None };] |> List.reduce (+)

            let take count (query : EntityQuery<'T>) =
                [query; { Filter = ""; TakeCount = Some count };] |> List.reduce (+)
        

        let convertToTableOperation operation =
            match operation with
            | Insert (entity) -> EntityTypeCache.CreateTableOperation.Value TableOperation.Insert entity null
            | InsertOrMerge (entity) -> EntityTypeCache.CreateTableOperation.Value TableOperation.InsertOrMerge entity null
            | InsertOrReplace (entity) -> EntityTypeCache.CreateTableOperation.Value TableOperation.InsertOrReplace entity null
            | Merge (entity, etag) -> EntityTypeCache.CreateTableOperation.Value TableOperation.Merge entity etag
            | ForceMerge (entity) -> EntityTypeCache.CreateTableOperation.Value TableOperation.Merge entity "*"
            | Replace (entity, etag) -> EntityTypeCache.CreateTableOperation.Value TableOperation.Replace entity etag
            | ForceReplace (entity) -> EntityTypeCache.CreateTableOperation.Value TableOperation.Replace entity "*"

        let private createBatchOperation operations = 
            let batchOperation = TableBatchOperation()
            do operations |> Seq.map convertToTableOperation |> Seq.iter batchOperation.Add
            batchOperation

        let private convertToOperationResult (result : TableResult) = 
            { HttpStatusCode = result.HttpStatusCode; Etag = result.Etag }


        let inTable (client: CloudTableClient) tableName operation = 
            let table = client.GetTableReference tableName
            operation |> convertToTableOperation |> table.Execute |> convertToOperationResult

        let inTableAsync (client: CloudTableClient) tableName operation = 
            async {
                let table = client.GetTableReference tableName
                let tableOperation = operation |> convertToTableOperation
                let! result = tableOperation |> table.ExecuteAsync |> Async.AwaitTask
                return result |> convertToOperationResult
            }
        
        let inTableAsBatch (client: CloudTableClient) tableName operations =
            let table = client.GetTableReference tableName
            let batchOperation = operations |> createBatchOperation
            let results = batchOperation |> table.ExecuteBatch
            results |> Seq.map convertToOperationResult |> Seq.toList

        let inTableAsBatchAsync (client: CloudTableClient) tableName operations =
            async {
                let table = client.GetTableReference tableName
                let batchOperation = operations |> createBatchOperation
                let! results = batchOperation |> table.ExecuteBatchAsync |> Async.AwaitTask
                return results |> Seq.map convertToOperationResult |> Seq.toList
            }

        let autobatch (operations : Operation<_> seq) = 
            operations 
            |> Seq.map (fun o -> o.GetEntity() |> !(EntityIdentiferReader.GetIdentifier), o)
            |> Seq.groupBy (fun (eId, _) -> eId.PartitionKey)
            |> Seq.map (fun (pk, ops) -> 
                let duplicates = 
                    ops 
                    |> Seq.countBy (fun (eId, _) -> eId.RowKey) 
                    |> Seq.filter (fun (rk, count) -> count > 1)
                    |> Seq.cache
                if duplicates |> Seq.isEmpty |> not then
                    let dupStr = duplicates |> Seq.fold (fun str (rk, _) -> str + sprintf "\r\n- '%s'" rk) ""
                    failwithf "Cannot automatically batch operations because multiple entities addressing the same rows exist for partition '%s' with row keys:%s" pk dupStr
                
                let arr = ops |> Seq.map snd |> Seq.toArray
                let windows = float(arr.Length) / float(MaxBatchSize) |> Math.Ceiling |> int
                seq { 
                    for i in 0 .. windows - 1 -> 
                        arr 
                        |> Array.window (i * MaxBatchSize) MaxBatchSize 
                        |> Seq.toList
                })
            |> Seq.concat
            |> Seq.toList
            
        let fromTable (client: CloudTableClient) name (query : EntityQuery<'T>) =
            let table = client.GetTableReference name
            let tableQuery = query.ToTableQuery()
            let resolver = EntityTypeCache.Resolver.Value //Do not inline this otherwise FSharp will delay execution of .Value until the resolver delegate is called
            table.ExecuteQuery<'T * EntityMetadata>(tableQuery, resolver)

        let fromTableSegmented (client: CloudTableClient) name continuationToken (query : EntityQuery<'T>) =
            let table = client.GetTableReference name
            let tableQuery = query.ToTableQuery()
            let resolver = EntityTypeCache.Resolver.Value //Do not inline this otherwise FSharp will delay execution of .Value until the resolver delegate is called
            let result = table.ExecuteQuerySegmented<'T * EntityMetadata>(tableQuery, resolver, continuationToken |> toNullRef)
            result.Results, result.ContinuationToken |> toOption

        let fromTableSegmentedAsync (client: CloudTableClient) name continuationToken (query : EntityQuery<'T>) =
            let table = client.GetTableReference name
            let tableQuery = query.ToTableQuery()
            let resolver = EntityTypeCache.Resolver.Value //Do not inline this otherwise FSharp will delay execution of .Value until the resolver delegate is called
            async {
                let! result = table.ExecuteQuerySegmentedAsync<'T * EntityMetadata>(tableQuery, resolver, continuationToken |> toNullRef) |> Async.AwaitTask
                return result.Results, result.ContinuationToken |> toOption
            }

        let fromTableAsync (client: CloudTableClient) name (query : EntityQuery<'T>) =
            let rec getSegmentAsync continutationToken resultsList =
                async {
                    let! result, furtherContinuation = query |> fromTableSegmentedAsync client name continutationToken
                    match furtherContinuation with
                    | Some _ -> return! result :: resultsList |> getSegmentAsync furtherContinuation
                    | None -> return result :: resultsList
                }
            async {
                let! resultsList = getSegmentAsync None []
                return resultsList |> List.rev |> Seq.concat
            }
