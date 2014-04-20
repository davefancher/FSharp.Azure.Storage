﻿namespace DigitallyCreated.FSharp.Azure.IntegrationTests

open System
open DigitallyCreated.FSharp.Azure.TableStorage
open Microsoft.WindowsAzure.Storage
open Microsoft.WindowsAzure.Storage.Table
open Xunit
open FsUnit.Xunit

module DataQuery =

    type Game = 
        { Name: string
          Platform: string
          Developer : string
          HasMultiplayer: bool }
          
          interface IEntityIdentifiable with
            member g.GetIdentifier() = 
                { PartitionKey = g.Developer; RowKey = g.Name + "-" + g.Platform }

    type TypeWithSystemProps = 
        { [<PartitionKey>] PartitionKey : string; 
          [<RowKey>] RowKey : string; 
          Timestamp : DateTimeOffset }

    type Simple = { [<PartitionKey>] PK : string; [<RowKey>] RK : string }
    
    type NonTableEntityClass() =
        member val Name : string = null with get,set

    type GameTableEntity() = 
        inherit Microsoft.WindowsAzure.Storage.Table.TableEntity()
        member val Name : string = null with get,set
        member val Platform : string = null with get,set
        member val Developer : string = null with get,set
        member val HasMultiplayer : bool = false with get,set

        override this.Equals other = 
            match other with
            | :? Game as game ->
                this.Name = game.Name && this.Platform = game.Platform && 
                this.Developer = game.Developer && this.HasMultiplayer = game.HasMultiplayer
            | :? GameTableEntity as game ->
                this.Name = game.Name && this.Platform = game.Platform && 
                this.Developer = game.Developer && this.HasMultiplayer = game.HasMultiplayer &&
                this.PartitionKey = game.PartitionKey && this.RowKey = game.RowKey &&
                this.Timestamp = game.Timestamp && this.ETag = game.ETag
            | _ -> false

        override this.GetHashCode() = 
            [box this.Name; box this.Platform; box this.Developer
             box this.HasMultiplayer; box this.PartitionKey; 
             box this.RowKey; box this.Timestamp; box this.HasMultiplayer ] 
                |> Seq.choose (fun o -> match o with | null -> None | o -> Some (o.GetHashCode()))
                |> Seq.reduce (^^^)


    type Tests() = 
        let account = CloudStorageAccount.Parse "UseDevelopmentStorage=true;"
        let tableClient = account.CreateCloudTableClient()
        let gameTableName = "TestsGame"
        let gameTable = tableClient.GetTableReference gameTableName

        do gameTable.DeleteIfExists() |> ignore
        do gameTable.Create() |> ignore

        let fromGameTable = fromTable tableClient gameTableName
        let fromGameTableAsync = fromTableAsync tableClient gameTableName

        static let data = [
            { Developer = "343 Industries"; Name = "Halo 4"; Platform = "Xbox 360"; HasMultiplayer = true }
            { Developer = "Bungie"; Name = "Halo 3"; Platform = "Xbox 360"; HasMultiplayer = true } 
            { Developer = "Bungie"; Name = "Halo 2"; Platform = "Xbox 360"; HasMultiplayer = true } 
            { Developer = "Bungie"; Name = "Halo 2"; Platform = "PC"; HasMultiplayer = true } 
            { Developer = "Bungie"; Name = "Halo 1"; Platform = "Xbox 360"; HasMultiplayer = true } 
            { Developer = "Bungie"; Name = "Halo 1"; Platform = "PC"; HasMultiplayer = true } 
            { Developer = "Valve"; Name = "Half-Life 2"; Platform = "PC"; HasMultiplayer = true } 
            { Developer = "Valve"; Name = "Portal"; Platform = "PC"; HasMultiplayer = false } 
            { Developer = "Valve"; Name = "Portal 2"; Platform = "PC"; HasMultiplayer = false } 
            { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "PC"; HasMultiplayer = true } 
            { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "Xbox 360"; HasMultiplayer = true }
            { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "PS3"; HasMultiplayer = true }  
            { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "Xbox One"; HasMultiplayer = true } 
            { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "PS4"; HasMultiplayer = true } 
        ]

        let insertInParallel tableName items = 
            items 
            |> Seq.map (fun r -> r |> Insert) 
            |> autobatch
            |> Seq.map (fun b -> b |> inTableAsBatchAsync tableClient tableName)
            |> Async.ParallelByDegree 4 
            |> Async.RunSynchronously
            |> Seq.concat
            |> Seq.iter (fun r -> r.HttpStatusCode |> should equal 204)

        do data |> insertInParallel gameTableName |> ignore

        let verifyMetadata metadata = 
            metadata |> Seq.iter (fun (_, m) ->
                m.Etag |> should not' (be NullOrEmptyString)
                m.Timestamp |> should not' (equal (DateTimeOffset()))
            )

        let verifyRecords expected actual = 
            actual |> Array.length |> should equal (expected |> Array.length)
            let actual = actual |> Seq.map fst
            actual |> Seq.iter (fun a -> expected |> Seq.exists (fun e -> a.Equals(e)) |> should equal true)



        [<Fact>]
        let ``query by specific instance``() = 
            let halo4 = 
                Query.all<Game> 
                |> Query.where <@ fun g s -> s.PartitionKey = "343 Industries" && s.RowKey = "Halo 4-Xbox 360" @> 
                |> fromGameTable
                |> Seq.toArray
            
            halo4 |> verifyRecords [|
                { Developer = "343 Industries"; Name = "Halo 4"; Platform = "Xbox 360"; HasMultiplayer = true }
            |]

            halo4 |> verifyMetadata

        [<Fact>]
        let ``query by partition key``() =
            let valveGames = 
                Query.all<Game> 
                |> Query.where <@ fun g s -> s.PartitionKey = "Valve" @> 
                |> fromGameTable 
                |> Seq.toArray
            
            valveGames |> verifyRecords [|
                { Developer = "Valve"; Name = "Half-Life 2"; Platform = "PC"; HasMultiplayer = true }
                { Developer = "Valve"; Name = "Portal"; Platform = "PC"; HasMultiplayer = false } 
                { Developer = "Valve"; Name = "Portal 2"; Platform = "PC"; HasMultiplayer = false }
            |]

            valveGames |> verifyMetadata

        [<Fact>]
        let ``query by properties``() =
            let valveGames = 
                Query.all<Game> 
                |> Query.where <@ fun g s -> (g.Platform = "Xbox 360" || g.Platform = "PC") && not (g.Developer = "Bungie") @> 
                |> fromGameTable 
                |> Seq.toArray
            
            valveGames |> verifyRecords [|
                { Developer = "343 Industries"; Name = "Halo 4"; Platform = "Xbox 360"; HasMultiplayer = true }
                { Developer = "Valve"; Name = "Half-Life 2"; Platform = "PC"; HasMultiplayer = true } 
                { Developer = "Valve"; Name = "Portal"; Platform = "PC"; HasMultiplayer = false } 
                { Developer = "Valve"; Name = "Portal 2"; Platform = "PC"; HasMultiplayer = false } 
                { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "PC"; HasMultiplayer = true } 
                { Developer = "Crystal Dynamics"; Name = "Tomb Raider"; Platform = "Xbox 360"; HasMultiplayer = true }
            |]

            valveGames |> verifyMetadata

        [<Fact>]
        let ``query with take``() =
            let valveGames = 
                Query.all<Game> 
                |> Query.where <@ fun g s -> s.PartitionKey = "Valve" @> 
                |> Query.take 2
                |> fromGameTable 
                |> Seq.toArray
            
            valveGames |> verifyRecords [|
                { Developer = "Valve"; Name = "Half-Life 2"; Platform = "PC"; HasMultiplayer = true } 
                { Developer = "Valve"; Name = "Portal 2"; Platform = "PC"; HasMultiplayer = false } 
            |]

            valveGames |> verifyMetadata

        [<Fact>]
        let ``async query``() =
            let valveGames = 
                Query.all<Game> 
                |> Query.where <@ fun g s -> s.PartitionKey = "Valve" @> 
                |> fromGameTableAsync 
                |> Async.RunSynchronously
                |> Seq.toArray
            
            valveGames |> verifyRecords [|
                { Developer = "Valve"; Name = "Half-Life 2"; Platform = "PC"; HasMultiplayer = true }
                { Developer = "Valve"; Name = "Portal"; Platform = "PC"; HasMultiplayer = false } 
                { Developer = "Valve"; Name = "Portal 2"; Platform = "PC"; HasMultiplayer = false }
            |]

            valveGames |> verifyMetadata

        let simpleTableName = "TestsSimple"
        let fromSimpleTableSegmented = fromTableSegmented tableClient simpleTableName
        let fromSimpleTableSegmentedAsync = fromTableSegmentedAsync tableClient simpleTableName
        let createDataForSegmentQueries() = 
            let simpleTable = tableClient.GetTableReference simpleTableName

            do simpleTable.DeleteIfExists() |> ignore
            do simpleTable.Create() |> ignore

            //Storage emulator segments the data after 1000 rows, so generate 1200 rows
            let rows =
                seq {
                    for partition in 1..12 do
                    for row in 1..100 do
                    yield { PK = "PK" + partition.ToString(); RK = "RK" + row.ToString() }
                }
                |> Array.ofSeq
            do rows |> insertInParallel simpleTableName
            rows

        [<Fact>]
        let ``segmented query``() =
            let rows = createDataForSegmentQueries();

            let (simples1, segmentToken1) = 
                Query.all<Simple> 
                |> fromSimpleTableSegmented None

            segmentToken1.IsSome |> should equal true

            let (simples2, segmentToken2) = 
                Query.all<Simple> 
                |> fromSimpleTableSegmented segmentToken1

            segmentToken2.IsNone |> should equal true

            let allSimples = [simples1; simples2] |> Seq.concat |> Seq.toArray
            allSimples |> verifyRecords rows
            allSimples |> verifyMetadata

        [<Fact>]
        let ``async segmented query``() =
            let rows = createDataForSegmentQueries();

            let (simples1, segmentToken1) = 
                Query.all<Simple> 
                |> fromSimpleTableSegmentedAsync None
                |> Async.RunSynchronously

            segmentToken1.IsSome |> should equal true

            let (simples2, segmentToken2) = 
                Query.all<Simple> 
                |> fromSimpleTableSegmentedAsync segmentToken1
                |> Async.RunSynchronously

            segmentToken2.IsNone |> should equal true

            let allSimples = [simples1; simples2] |> Seq.concat |> Seq.toArray
            allSimples |> verifyRecords rows
            allSimples |> verifyMetadata

        [<Fact>]
        let ``query with a type that has system properties on it``() =

            let valveGames = 
                Query.all<TypeWithSystemProps> 
                |> Query.where <@ fun g s -> s.PartitionKey = "Valve" @> 
                |> fromTable tableClient gameTableName
                |> Seq.toArray
            
            valveGames |> Array.iter (fun (g, _) -> g.PartitionKey |> should equal "Valve")
            valveGames |> Array.exists (fun (g, _) -> g.RowKey = "Half-Life 2-PC" ) |> should equal true
            valveGames |> Array.exists (fun (g, _) -> g.RowKey = "Portal-PC" ) |> should equal true
            valveGames |> Array.exists (fun (g, _) -> g.RowKey = "Portal 2-PC" ) |> should equal true
            valveGames |> Array.iter (fun (g, _) -> g.Timestamp |> should not' (equal (DateTimeOffset())))

            valveGames |> verifyMetadata

        [<Fact>]
        let ``query with a table entity type``() =
            let valveGames = 
                Query.all<GameTableEntity> 
                |> Query.where <@ fun g s -> s.PartitionKey = "Valve" @> 
                |> fromTable tableClient gameTableName
                |> Seq.toArray
            
            valveGames |> verifyRecords [|
                { Developer = "Valve"; Name = "Half-Life 2"; Platform = "PC"; HasMultiplayer = true }
                { Developer = "Valve"; Name = "Portal"; Platform = "PC"; HasMultiplayer = false } 
                { Developer = "Valve"; Name = "Portal 2"; Platform = "PC"; HasMultiplayer = false }
            |]

            valveGames |> Array.iter (fun (g, _) -> g.PartitionKey |> should equal "Valve")
            valveGames |> Array.exists (fun (g, _) -> g.RowKey = "Half-Life 2-PC" ) |> should equal true
            valveGames |> Array.exists (fun (g, _) -> g.RowKey = "Portal-PC" ) |> should equal true
            valveGames |> Array.exists (fun (g, _) -> g.RowKey = "Portal 2-PC" ) |> should equal true
            valveGames |> Array.iter (fun (g, _) -> g.Timestamp |> should not' (equal (DateTimeOffset())))

            valveGames |> verifyMetadata

        [<Fact>]
        let ``querying with types that aren't records or implement ITableEntity fails``() = 
            (fun () -> Query.all<NonTableEntityClass> |> fromTable tableClient gameTableName |> ignore)
                |> should throw typeof<Exception>

