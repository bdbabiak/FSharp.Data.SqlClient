﻿namespace  FSharp.Data.SqlClient

open Microsoft.FSharp.Core.CompilerServices
open Samples.FSharp.ProvidedTypes
open System
open System.Configuration
open System.Threading
open System.Data
open System.Collections.Generic
open System.Data.SqlClient
open System.Reflection
open System.IO
open Microsoft.FSharp.Quotations
open Microsoft.FSharp.Reflection

type ResultSetType =
    | Tuples = 0
    | Records = 1
    | DataTable = 3

[<Sealed>]
type DataTable<'T when 'T :> DataRow>() = 
    inherit DataTable() 
    //inherit TypedTableBase<'T>() 

    member this.Item index : 'T = downcast this.Rows.[index] 

    interface ICollection<'T> with
        member this.GetEnumerator() = this.Rows.GetEnumerator()
        member this.GetEnumerator() : IEnumerator<'T> = (Seq.cast<'T> this.Rows).GetEnumerator() 
        member this.Count = this.Rows.Count
        member this.IsReadOnly = this.Rows.IsReadOnly
        member this.Add row = this.Rows.Add row
        member this.Clear() = this.Rows.Clear()
        member this.Contains row = this.Rows.Contains row
        member this.CopyTo(dest, index) = this.Rows.CopyTo(dest, index)
        member this.Remove row = this.Rows.Remove(row); true

//    later
//    interface IReadOnlyList<DataRow> with
//        member this.Item with get index = this.Rows.[index]


[<TypeProvider>]
type public SqlCommandTypeProvider(config : TypeProviderConfig) as this = 
    inherit TypeProviderForNamespaces()

    let runtimeAssembly = Assembly.LoadFrom(config.RuntimeAssembly)

    let nameSpace = this.GetType().Namespace
    let assembly = Assembly.GetExecutingAssembly()
    let providerType = ProvidedTypeDefinition(assembly, nameSpace, "SqlCommand", Some typeof<obj>, HideObjectMethods = true)
    let sqlEngineTypeToClrMap = ref Map.empty

    do
        AppDomain.CurrentDomain.add_AssemblyResolve(fun _ args ->
            let name = AssemblyName(args.Name)
            let existingAssembly = 
                AppDomain.CurrentDomain.GetAssemblies()
                |> Seq.tryFind(fun a -> AssemblyName.ReferenceMatchesDefinition(name, a.GetName()))
            match existingAssembly with
            | Some a -> a
            | None -> null
        )

    do 
        providerType.DefineStaticParameters(
            parameters = [ 
                ProvidedStaticParameter("CommandText", typeof<string>) 
                ProvidedStaticParameter("ConnectionString", typeof<string>, "") 
                ProvidedStaticParameter("ConnectionStringName", typeof<string>, "") 
                ProvidedStaticParameter("ResultSetType", typeof<ResultSetType>, ResultSetType.Tuples) 
                ProvidedStaticParameter("SingleRow", typeof<bool>, false) 
                ProvidedStaticParameter("ConfigFile", typeof<string>, "app.config") 
                ProvidedStaticParameter("DataDirectory", typeof<string>, "") 
            ],             
            instantiationFunction = this.CreateType
        )
        this.AddNamespace(nameSpace, [ providerType ])

    member internal this.CreateType typeName parameters = 
        let commandText : string = unbox parameters.[0] 
        let connectionString : string = unbox parameters.[1] 
        let connectionStringName : string = unbox parameters.[2] 
        let resultSetType : ResultSetType = unbox parameters.[3] 
        let singleRow : bool = unbox parameters.[4] 
        let configFile : string = unbox parameters.[5] 
        let dataDirectory : string = unbox parameters.[6] 
        let resolutionFolder = config.ResolutionFolder

        let connectionString =  ConnectionString.resolve (config.ResolutionFolder) connectionString connectionStringName configFile
        this.CheckMinimalVersion connectionString
        this.LoadDataTypesMap connectionString

        let commandType = ProvidedTypeDefinition(assembly, nameSpace, typeName, baseType = Some typeof<obj>, HideObjectMethods = true)

        let parameters = this.ExtractParameters(connectionString, commandText)

        ProvidedConstructor(
            parameters = [],
            InvokeCode = fun _ -> 
                <@@ 
                    let connectionString = ConnectionString.resolve resolutionFolder connectionString connectionStringName configFile
                    if dataDirectory <> ""
                    then AppDomain.CurrentDomain.SetData("DataDirectory", dataDirectory)
                    let this = new SqlCommand(commandText, new SqlConnection(connectionString)) 
                    for x in parameters do
                        let xs = x.Split(',') 
                        let paramName, sqlEngineTypeName = xs.[0], xs.[2]
                        let sqlEngineTypeNameWithoutSize = 
                            let openParentPos = sqlEngineTypeName.IndexOf('(')
                            if openParentPos = -1 then sqlEngineTypeName else sqlEngineTypeName.Substring(0, openParentPos)
                        let dbType = Enum.Parse(typeof<SqlDbType>, sqlEngineTypeNameWithoutSize, ignoreCase = true) |> unbox
                        this.Parameters.Add(paramName, dbType) |> ignore
                    this
                @@>
        ) 
        |> commandType.AddMember 

        commandType.AddMembersDelayed <| fun() -> 
            parameters
            |> List.map (fun x -> 
                let paramName, clrTypeName = let xs = x.Split(',') in xs.[0], xs.[1]
                assert (paramName.StartsWith "@")

                let prop = ProvidedProperty(propertyName = paramName.Substring 1, propertyType = Type.GetType clrTypeName)
                prop.GetterCode <- fun args -> 
                    <@@ 
                        let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                        sqlCommand.Parameters.[paramName].Value
                    @@>

                prop.SetterCode <- fun args -> 
                    <@@ 
                        let sqlCommand : SqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>)
                        sqlCommand.Parameters.[paramName].Value <- %%Expr.Coerce(args.[1], typeof<obj>)
                    @@>

                prop
            )

        use conn = new SqlConnection(connectionString)
        use cmd = new SqlCommand("sys.sp_describe_first_result_set", conn, CommandType = CommandType.StoredProcedure)
        cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
        conn.Open()
        use reader = cmd.ExecuteReader()
        if not reader.HasRows
        then 
            this.AddExecuteNonQuery commandType
        else
            this.AddExecuteReader(reader, commandType, resultSetType, singleRow)
        commandType
    
    member __.ExtractParameters(connectionString, commandText) : string list =  
        [
            use conn = new SqlConnection(connectionString)
            conn.Open()

            use cmd = new SqlCommand("sys.sp_describe_undeclared_parameters", conn, CommandType = CommandType.StoredProcedure)
            cmd.Parameters.AddWithValue("@tsql", commandText) |> ignore
            use reader = cmd.ExecuteReader()
            while(reader.Read()) do
                let paramName = string reader.["name"]
                let clrTypeName = this.MapSqlEngineTypeToClr(sqlEngineTypeId = unbox<int> reader.["suggested_system_type_id"], detailedMessage = " Parameter name:" + paramName)
                let dbTypeName = string reader.["suggested_system_type_name"]
                yield sprintf "%s,%s,%s" paramName clrTypeName dbTypeName
        ]

    member __.CheckMinimalVersion connectionString = 
        use conn = new SqlConnection(connectionString)
        conn.Open()
        let majorVersion = conn.ServerVersion.Split('.').[0]
        if int majorVersion < 11 then failwithf "Minimal supported major version is 11. Currently used: %s" conn.ServerVersion

    member __.LoadDataTypesMap connectionString = 
        if sqlEngineTypeToClrMap.Value.IsEmpty
        then
            use conn = new SqlConnection(connectionString)
            conn.Open()
            sqlEngineTypeToClrMap := query {
                let getSysTypes = new SqlCommand("SELECT * FROM sys.types", conn)
                for x in conn.GetSchema("DataTypes").AsEnumerable() do
                join y in (getSysTypes.ExecuteReader(CommandBehavior.CloseConnection) |> Seq.cast<IDataRecord>) on 
                    (x.Field("TypeName") = string y.["name"])
                let system_type_id = y.["system_type_id"] |> unbox<byte> |> int 
                select(system_type_id, x.Field<string>("DataType"))
            }
            |> Map.ofSeq

    member __.MapSqlEngineTypeToClr(sqlEngineTypeId, detailedMessage) = 
        match !sqlEngineTypeToClrMap |> Map.tryFind sqlEngineTypeId with
        | Some clrType ->  clrType
        | None -> failwithf "Cannot map sql engine type %i to CLR type. %s" sqlEngineTypeId detailedMessage

    member internal __.AddExecuteNonQuery commandType = 
        let execute = ProvidedMethod("Execute", [], typeof<Async<unit>>)
        execute.InvokeCode <- fun args ->
            <@@
                async {
                    let sqlCommand = %%Expr.Coerce(args.[0], typeof<SqlCommand>) : SqlCommand
                    //open connection async on .NET 4.5
                    use conn = sqlCommand.Connection
                    conn.Open()
                    return! sqlCommand.AsyncExecuteNonQuery() 
                }
            @@>
        commandType.AddMember execute

    static member internal GetDataReader(cmd, singleRow) = 
        let commandBehavior = if singleRow then CommandBehavior.SingleRow  else CommandBehavior.Default 
        <@@ 
            async {
                let sqlCommand : SqlCommand = %%Expr.Coerce(cmd, typeof<SqlCommand>)
                //open connection async on .NET 4.5
                sqlCommand.Connection.Open()
                return!
                    try 
                        sqlCommand.AsyncExecuteReader(commandBehavior ||| CommandBehavior.CloseConnection ||| CommandBehavior.SingleResult)
                    with _ ->
                        sqlCommand.Connection.Close()
                        reraise()
            }
        @@>

    static member internal GetRows(cmd, singleRow) = 
        let commandBehavior = if singleRow then CommandBehavior.SingleRow  else CommandBehavior.Default 
        <@@ 
            async {
                let sqlCommand : SqlCommand = %%Expr.Coerce(cmd, typeof<SqlCommand>)
                let! token = Async.CancellationToken
                let! (reader : SqlDataReader) = %%SqlCommandTypeProvider.GetDataReader(cmd, singleRow)
                return seq {
                    try 
                        while(not token.IsCancellationRequested && reader.Read()) do
                            let row = Array.zeroCreate reader.FieldCount
                            reader.GetValues row |> ignore
                            yield row  
                    finally
                        sqlCommand.Connection.Close()
                } |> Seq.cache
            }
        @@>

    static member internal GetTypedSequence<'Row>(cmd, rowMapper, singleRow) = 
        let getTypedSeqAsync = 
            <@@
                async { 
                    let! (rows : seq<obj[]>) = %%SqlCommandTypeProvider.GetRows(cmd, singleRow)
                    return Seq.map (%%rowMapper : obj[] -> 'Row) rows
                }
                
            @@>

        if singleRow
        then 
            <@@ 
                async { 
                    let! xs  = %%getTypedSeqAsync : Async<'Row seq>
                    return Seq.exactlyOne xs
                }
            @@>
        else
            getTypedSeqAsync
            

    static member internal SelectOnlyColumn0<'Row>(cmd, singleRow) = 
        SqlCommandTypeProvider.GetTypedSequence<'Row>(cmd, <@ fun (values : obj[]) -> unbox<'Row> values.[0] @>, singleRow)

    static member internal GetTypedDataTable<'T when 'T :> DataRow>(cmd, singleRow)  = 
        <@@
            async {
                let! (reader : SqlDataReader) = %%SqlCommandTypeProvider.GetDataReader(cmd, singleRow)
                let table = new DataTable<'T>() 
                table.Load reader
                return table
            }
        @@>

    member internal __.AddExecuteReader(columnInfoReader, commandType, resultSetType, singleRow) = 
        let columns = 
            columnInfoReader 
            |> Seq.cast<IDataRecord> 
            |> Seq.map (fun x -> 
                let columnName = string x.["name"]
                columnName, 
                this.MapSqlEngineTypeToClr(sqlEngineTypeId = unbox x.["system_type_id"], detailedMessage = " Column name:" + columnName),
                unbox<int> x.["column_ordinal"]
            ) 
            |> Seq.toList

        if columns.Length = 1
        then
            let _, itemTypeName, _ = columns.Head

            let itemType = Type.GetType itemTypeName
            let returnType = 
                let asyncSpecialization = if singleRow then itemType else typedefof<_ seq>.MakeGenericType itemType 
                typedefof<_ Async>.MakeGenericType asyncSpecialization

            let execute = ProvidedMethod("Execute", [], returnType)

            execute.InvokeCode <- fun args -> 
                let impl = this.GetType().GetMethod("SelectOnlyColumn0", BindingFlags.NonPublic ||| BindingFlags.Static).MakeGenericMethod([| itemType |])
                impl.Invoke(null, [| args.[0]; singleRow |]) |> unbox
            commandType.AddMember execute

        else 
            let syncReturnType, executeMethodBody = 
                match resultSetType with 

                | ResultSetType.Tuples ->
                    let tupleType = columns |> List.map (fun(_, typeName, _) -> Type.GetType typeName) |> List.toArray |> FSharpType.MakeTupleType
                    let rowMapper = 
                        let values = Var("values", typeof<obj[]>)
                        let getTupleType = Expr.Call(typeof<Type>.GetMethod("GetType", [| typeof<string>|]), [ Expr.Value tupleType.AssemblyQualifiedName ])
                        Expr.Lambda(values, Expr.Coerce(Expr.Call(typeof<FSharpValue>.GetMethod("MakeTuple"), [Expr.Var values; getTupleType]), tupleType))
                    let getExecuteBody(args : Expr list) = 
                        this.GetType()
                            .GetMethod("GetTypedSequence", BindingFlags.NonPublic ||| BindingFlags.Static)
                            .MakeGenericMethod([| tupleType |])
                            .Invoke(null, [| args.[0]; rowMapper; singleRow |]) 
                            |> unbox

                    let resultType = if singleRow then tupleType else typedefof<_ seq>.MakeGenericType(tupleType)
                    resultType, getExecuteBody

                | ResultSetType.Records -> 
                    let rowType = ProvidedTypeDefinition("Row", baseType = Some typeof<obj>, HideObjectMethods = true)
                    for name, propertyTypeName, columnOrdinal  in columns do
                        if name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." columnOrdinal
                        let property = ProvidedProperty(name, propertyType = Type.GetType propertyTypeName) 
                        property.GetterCode <- fun args -> 
                            <@@ 
                                let values : obj[] = %%Expr.Coerce(args.[0], typeof<obj[]>)
                                values.[columnOrdinal - 1]
                            @@>

                        rowType.AddMember property

                    commandType.AddMember rowType
                    let resultType = if singleRow then rowType :> Type else typedefof<_ seq>.MakeGenericType(rowType)
                    let getExecuteBody (args : Expr list) = 
                        SqlCommandTypeProvider.GetTypedSequence(args.[0], <@ fun(values : obj[]) -> box values @>, singleRow)
                         
                    resultType, getExecuteBody

                | ResultSetType.DataTable ->
                    //let rowType = typeof<DataRow>
                    let rowType = ProvidedTypeDefinition("Row", Some typeof<DataRow>)
                    for name, propertyTypeName, columnOrdinal  in columns do
                        if name = "" then failwithf "Column #%i doesn't have name. Only columns with names accepted. Use explicit alias." columnOrdinal
                        let property = ProvidedProperty(name, propertyType = Type.GetType propertyTypeName) 
                        property.GetterCode <- fun args -> <@@ (%%args.[0] : DataRow).[name] @@>
                        property.SetterCode <- fun args -> <@@ (%%args.[0] : DataRow).[name] <- box %%args.[1] @@>

                        rowType.AddMember property

                    let resultType = typedefof<_ DataTable>.MakeGenericType rowType 
                    //let resultType = ProvidedTypeBuilder.MakeGenericType(typedefof<_ DataTable>, [ rowType ])
                    commandType.AddMembers [ rowType :> Type; resultType ]

                    let getExecuteBody(args : Expr list) = 
                        let impl = this.GetType().GetMethod("GetTypedDataTable", BindingFlags.NonPublic ||| BindingFlags.Static).MakeGenericMethod([| typeof<DataRow> |])
//                        let impl = ProvidedTypeBuilder.MakeGenericMethod(this.GetType().GetMethod("GetTypedDataTable", BindingFlags.NonPublic ||| BindingFlags.Static), [ rowType :> Type ])
                        impl.Invoke(null, [| args.[0]; singleRow |]) |> unbox

                    resultType, getExecuteBody

                | _ -> failwith "Unexpected"
                    
            commandType.AddMember <| ProvidedMethod("Execute", [], typedefof<_ Async>.MakeGenericType syncReturnType, InvokeCode = executeMethodBody)

[<assembly:TypeProviderAssembly>]
do()
