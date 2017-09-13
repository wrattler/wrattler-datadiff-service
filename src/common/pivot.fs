module TheGamma.Services.Pivot

open System
open FSharp.Data
open Suave

// ------------------------------------------------------------------------------------------------
// Types for values and transformations
// ------------------------------------------------------------------------------------------------

type Value = 
  | Bool of bool
  | String of string
  | Number of float
  | Date of DateTimeOffset

[<RequireQualifiedAccess>]
type GroupAggregation = 
  | GroupKey
  | CountAll
  | CountDistinct of string
  | ConcatValues of string
  | Sum of string
  | Mean of string

[<RequireQualifiedAccess>]
type WindowAggregation = 
  | Min of string
  | Max of string
  | Mean of string
  | Sum of string
  | FirstKey
  | LastKey
  | MiddleKey

type SortDirection =
  | Ascending
  | Descending 

type Paging =
  | Take of int
  | Skip of int
  
type FilterOperator = 
  | And | Or

type RelationalOperator = 
  | Equals 
  | NotEquals 
  | LessThan
  | GreaterThan 
  | InRange
  | Like

type FilterCondition = RelationalOperator * string * string

type Transformation = 
  | DropColumns of string list
  | SortBy of (string * SortDirection) list
  | GroupBy of string list * GroupAggregation list
  | WindowBy of string * int * WindowAggregation list
  | ExpandBy of string * WindowAggregation list
  | FilterBy of FilterOperator * FilterCondition list
  | Paging of Paging list
  | Empty

type Action = 
  | Metadata
  | GetSeries of string * string
  | GetTheData 
  | GetRange of string

type Query = 
  { Transformations : Transformation list
    Action : Action }


// ------------------------------------------------------------------------------------------------
// Parsing transformations
// ------------------------------------------------------------------------------------------------

module Transform = 

  let groupUnaryOps = 
    [ "count-dist", GroupAggregation.CountDistinct; "mean", GroupAggregation.Mean
      "concat-vals", GroupAggregation.ConcatValues; "sum", GroupAggregation.Sum ]
  let groupNullaryOps =
    [ "key", GroupAggregation.GroupKey; "count-all", GroupAggregation.CountAll ]

  let winUnaryOps = 
    [ "mean", WindowAggregation.Mean; "sum", WindowAggregation.Sum
      "min", WindowAggregation.Min; "max", WindowAggregation.Max ]
  let winNullaryOps = 
    [ "first-key", WindowAggregation.FirstKey; "last-key", WindowAggregation.LastKey
      "mid-key", WindowAggregation.MiddleKey ]

  let trimIdent (s:string) = 
    if s.StartsWith("'") && s.EndsWith("'") then s.Substring(1, s.Length-2)
    else s

  let parseOp nullary unary (op:string) =
    match nullary |> List.tryFind (fun (k, f) -> k = op) with
    | Some(_, op) -> op
    | _ ->
      let parsed = unary |> List.tryPick (fun (k, f) ->
        if op.StartsWith(k) then Some(f(trimIdent(op.Substring(k.Length + 1))))
        else None)
      if parsed.IsSome then parsed.Value else failwith "Unknonw operation"

  let parseAction (op, args) = 
    match op, args with
    | "metadata", [] -> Metadata, true
    | "series", [k; v] -> GetSeries(k, v), true
    | "range", [f] -> GetRange(f), true
    | _ -> GetTheData, false

  let operators = 
    [ Equals, " eq "; NotEquals, " neq "; LessThan, " lte "; GreaterThan, " gte "; InRange, " in "; Like, " like " ]

  let parseCondition (cond:string) = 
    let cond = cond.Trim()
    let start = if cond.StartsWith("'") then cond.IndexOf('\'', 1) else 0
    let optRes = 
      operators |> Seq.tryPick (fun (op, s) ->
        let i = cond.IndexOf(s)
        if i = -1 then None else
          Some(op, trimIdent (cond.Substring(0, i)), trimIdent (cond.Substring(i + s.Length))))
    match optRes with
    | None -> failwithf "Incorrectly formatted condition: >>%s<<" cond
    | Some res -> res

  let parseTransform (op, args) = 
    match op, args with
    | "drop", columns -> DropColumns(List.map trimIdent columns)
    | "sort", columns -> SortBy(columns |> List.map (fun col -> 
        if col.EndsWith(" asc") then trimIdent (col.Substring(0, col.Length-4)), Ascending
        elif col.EndsWith(" desc") then trimIdent (col.Substring(0, col.Length-5)), Descending
        else trimIdent col, Ascending))
    | "filter", "and"::conds -> FilterBy(And, List.map parseCondition conds)
    | "filter", "or"::conds -> FilterBy(Or, List.map parseCondition conds)
    | "filter", conds -> FilterBy(And, List.map parseCondition conds)
    | "groupby", ops ->
        let keys = ops |> List.takeWhile (fun s -> s.StartsWith "by ") |> List.map (fun s -> trimIdent (s.Substring(3)))
        let aggs = ops |> List.skipWhile (fun s -> s.StartsWith "by ") |> List.map (parseOp groupNullaryOps groupUnaryOps)
        GroupBy(keys, aggs)
    | "windowby", key::size::ops ->
        let key = trimIdent (key.Substring(3))
        let aggs = ops |> List.map (parseOp winNullaryOps winUnaryOps)
        WindowBy(key, int size, aggs)
    | "expandby", key::ops ->
        let key = trimIdent (key.Substring(3))
        let aggs = ops |> List.map (parseOp winNullaryOps winUnaryOps)
        ExpandBy(key, aggs)
    | "take", [n] -> Paging [Take (int n)]
    | "skip", [n] -> Paging [Skip (int n)]
    | _ -> failwith "Unsupported transformation"

  let parseArgs (s:string) = 
    let rec loop i quoted current acc = 
      let parseCurrent () = System.String(Array.ofList (List.rev current))
      if i = s.Length then List.rev (parseCurrent()::acc) else
      let c = s.[i]
      if c = '\'' && quoted then loop (i + 1) false (c::current) acc
      elif c = '\'' && not quoted then loop (i + 1) true (c::current) acc
      elif c = ',' && not quoted then loop (i + 1) quoted [] (parseCurrent()::acc)
      else loop (i + 1) quoted (c::current) acc
    loop 0 false [] [] 

  let parseChunk (s:string) =
    let openPar, closePar = s.IndexOf('('), s.LastIndexOf(')')
    if openPar = -1 || closePar = -1 then s, []
    else s.Substring(0, openPar), parseArgs (s.Substring(openPar + 1, closePar - openPar - 1))
    
  let fromUrl (s:string) = 
    let chunks = 
      System.Web.HttpUtility.UrlDecode(s)
        .Split([|'$'|], StringSplitOptions.RemoveEmptyEntries) 
      |> Array.map parseChunk 
    if chunks.Length = 0 then { Transformations = []; Action = GetTheData }
    else
      let action, explicit = parseAction (chunks.[chunks.Length - 1])
      let chunks = List.ofArray (if explicit then chunks.[0 .. chunks.Length - 2] else chunks)
      { Transformations = List.map parseTransform chunks; Action = action }    

// ------------------------------------------------------------------------------------------------
// Serialize JsonValues
// ------------------------------------------------------------------------------------------------

module Serializer = 
  let serializeValue = function 
    | Value.String s -> JsonValue.String s 
    | Value.Bool b -> JsonValue.Boolean b
    | Value.Number n -> JsonValue.Float n
    | Value.Date d -> JsonValue.String (d.ToString "o")

  let serialize isSeries data = 
    data 
    |> Array.map (fun (fields:_[]) ->
      if isSeries then
        JsonValue.Array [| serializeValue (snd fields.[0]); serializeValue (snd fields.[1]) |]
      else 
        fields |> Array.map (fun (k, v) -> k, serializeValue v) |> JsonValue.Record)
    |> JsonValue.Array

// ------------------------------------------------------------------------------------------------
// Evaluate query
// ------------------------------------------------------------------------------------------------

module Evaluator = 
  let inline pickField name obj = 
    Array.pick (fun (n, v) -> if n = name then Some v else None) obj

  let asString = function String s -> s | Number n -> string n | Date d -> d.ToString("g") | Bool b -> string b
  let asFloat = function String s -> float s | Number n -> n | Date d -> float d.Ticks | Bool true -> 1. | Bool false -> 0.

  let applyGroupAggregation kvals group = function
   | GroupAggregation.GroupKey -> kvals
   | GroupAggregation.CountAll -> [ "count", Number(group |> Seq.length |> float) ]
   | GroupAggregation.CountDistinct(fld) -> [ fld, Number(group |> Seq.distinctBy (pickField fld) |> Seq.length |> float) ]
   | GroupAggregation.ConcatValues(fld) -> [ fld, group |> Seq.map(fun obj -> pickField fld obj |> asString) |> Seq.distinct |> String.concat ", " |> String ]
   | GroupAggregation.Sum(fld) -> [ fld, group |> Seq.sumBy (fun obj -> pickField fld obj |> asFloat) |> Number ]
   | GroupAggregation.Mean(fld) -> [ fld, group |> Seq.averageBy (fun obj -> pickField fld obj |> asFloat) |> Number ]

  let applyWinAggregation kname group agg = 
    let nums fld = group |> Seq.map (fun obj -> pickField fld obj |> asFloat)
    let kvalues = Seq.map (pickField kname) group |> Array.ofSeq
    match agg with
    | WindowAggregation.Mean(fld) -> [ fld, nums fld |> Seq.average |> Number ]
    | WindowAggregation.Min(fld) -> [ fld, nums fld |> Seq.min |> Number ]
    | WindowAggregation.Max(fld) -> [ fld, nums fld |> Seq.max |> Number ]
    | WindowAggregation.Sum(fld) -> [ fld, nums fld |> Seq.sum |> Number ]
    | WindowAggregation.FirstKey -> [ "first " + kname, kvalues.[0] ]
    | WindowAggregation.LastKey -> [ "last " + kname, kvalues.[kvalues.Length-1] ]
    | WindowAggregation.MiddleKey -> [ "middle " + kname, kvalues.[(kvalues.Length-1)/2] ]

  let getExpandAggregationFunction kname agg = 
    let ret fld f = fun row -> fld, pickField fld row |> asFloat |> f |> Number
    match agg with
    | WindowAggregation.Mean(fld) -> 
        let mutable sum = 0.
        let mutable count = 0.
        ret fld (fun v -> sum <- sum + v; count <- count + v; sum / count)
    | WindowAggregation.Min(fld) -> 
        let mutable min = Double.MaxValue
        ret fld (fun v -> (if v < min then min <- v); min)
    | WindowAggregation.Max(fld) ->
        let mutable max = Double.MinValue
        ret fld (fun v -> (if v > max then max <- v); max)
    | WindowAggregation.Sum(fld) -> 
        let mutable sum = 0.
        ret fld (fun v -> sum <- sum + v; sum)
    | WindowAggregation.FirstKey -> 
        let mutable first = None
        fun row -> (if first.IsNone then first <- Some (pickField kname row)); "first " + kname, first.Value
    | WindowAggregation.LastKey -> 
        fun row -> "last " + kname, pickField kname row
    | WindowAggregation.MiddleKey -> 
        let keys = ResizeArray<_>()
        fun row -> keys.Add(pickField kname row); "middle " + kname, keys.[keys.Count/2]

  let compareFields o1 o2 (fld, order) = 
    let reverse = if order = Descending then -1 else 1
    match pickField fld o1, pickField fld o2 with
    | Number d1, Number d2 -> reverse * compare d1 d2
    | String s1, String s2 -> reverse * compare s1 s2
    | Date d1, Date d2 -> reverse * compare d1 d2
    | _ -> failwith "Cannot compare values"

  let evalCondition op actual (expected:string) =
    match op, actual with 
    | Like, String s -> s.ToLower().Contains(expected.ToLower())
    | Like, _ -> failwith "Like can only be used on strings"
    | InRange, Date dt ->
        let exp = expected.Split(',') 
        match System.DateTimeOffset.TryParse(exp.[0]), System.DateTimeOffset.TryParse(exp.[1]) with
        | (true, dt1), (true, dt2) -> dt >= dt1 && dt <= dt2
        | _ -> failwithf "Value '%s' is not a valid date or time rnge." expected
    | _, Date dt1 -> 
        match System.DateTimeOffset.TryParse(expected), op with
        | (false, _), _ -> failwithf "Value '%s' is not a valid date or time." expected
        | (_, dt2), Equals -> dt1 = dt2
        | (_, dt2), NotEquals -> dt1 <> dt2
        | (_, dt2), LessThan -> dt1 < dt2
        | (_, dt2), GreaterThan -> dt1 > dt2
        | (_, dt2), InRange -> failwith "evalCondition: Unexpected InRnge"
        | (_, dt2), Like -> failwith "evalCondition: Unexpected Like"
    | Equals, Bool b -> (expected.ToLower() = "true" && b) || (expected.ToLower() = "false" && not b)
    | NotEquals, Bool b -> (expected.ToLower() = "true" && not b) || (expected.ToLower() = "false" && b)
    | Equals, String s -> expected = s
    | NotEquals, String s -> expected <> s
    | (Equals | NotEquals), Number _ -> failwith "Equals and not equals work only on strings or booleans"
    | GreaterThan, Number n -> n > float expected
    | LessThan, Number n -> n < float expected
    | InRange, Number n -> let expected = expected.Split(',') in n >= float expected.[0] && n <= float expected.[1]
    | (GreaterThan | LessThan | InRange), (Bool _ | String _) -> failwith "Relational operator work only on numbers"

  let transformData (objs:seq<(string * Value)[]>) = function
    | Empty -> objs
    | Paging(pgops) ->
        pgops |> Seq.fold (fun objs -> function
          | Take n -> objs |> Seq.truncate n
          | Skip n -> objs |> Seq.skip n) objs
    | DropColumns(flds) ->
        let dropped = set flds
        objs |> Seq.map (fun obj ->
          obj |> Array.filter (fst >> dropped.Contains >> not))
    | SortBy(flds) ->
        let flds = List.rev flds
        objs |> Seq.sortWith (fun o1 o2 ->
          let optRes = flds |> List.map (compareFields o1 o2) |> List.skipWhile ((=) 0) |> List.tryHead
          defaultArg optRes 0)
    | FilterBy(op, conds) ->
        objs |> Seq.filter (fun o ->
          let f = match op with And -> Seq.forall | Or -> Seq.exists
          conds |> f (fun (op, fld, value) -> evalCondition op (pickField fld o) value))
    | WindowBy(fld, size, aggs) ->
        objs 
        |> Seq.sortBy (pickField fld)
        |> Seq.windowed size 
        |> Seq.map (fun win ->
          aggs
          |> List.collect (applyWinAggregation fld win)
          |> Array.ofSeq )
    | ExpandBy(fld, aggs) ->
        let funcs = aggs |> Seq.map (getExpandAggregationFunction fld) |> Array.ofSeq
        objs 
        |> Seq.sortBy (pickField fld)
        |> Seq.map (fun row ->
            funcs |> Array.map (fun f -> f row))
    | GroupBy(flds, aggs) ->
        objs 
        |> Seq.groupBy (fun j -> List.map (fun f -> pickField f j) flds)
        |> Seq.map (fun (kvals, group) ->
          aggs 
          |> List.collect (applyGroupAggregation (List.zip flds kvals) group)
          |> Array.ofSeq )


// ------------------------------------------------------------------------------------------------
// Schema inference and CSV file parsing
// ------------------------------------------------------------------------------------------------

exception ParseError of string

type InferredType =   
  | Any | String | Int | Float
  | Bool | OneZero 
  | Date of System.Globalization.CultureInfo

module Inference = 
  let parseError s = raise (ParseError s)

  let tryDate culture s = 
    DateTimeOffset.TryParse(s, culture, Globalization.DateTimeStyles.AssumeUniversal) |> fst

  let mmdd = System.Globalization.CultureInfo.InvariantCulture 
  let ddmm = System.Globalization.CultureInfo.GetCultureInfo("en-gb")
  
  let inferType (s:string) =
    if fst (Int32.TryParse(s)) then 
      let i = Int32.Parse(s) 
      if i = 1 || i = 0 then OneZero else Int
    elif fst (Decimal.TryParse(s)) then Float
    elif tryDate mmdd s && tryDate ddmm s then Date null
    elif tryDate mmdd s then Date mmdd
    elif tryDate ddmm s then Date ddmm
    elif s.ToLower() = "true" || s.ToLower() = "false" then Bool
    else String

  let unifyTypes t1 t2 = 
    match t1, t2 with
    | _ when t1 = t2 -> t1
    | Any, t | t, Any -> t
    | Date c, Date null | Date null, Date c -> Date c
    | Date c1, Date c2 when c1 = c2 -> Date c1
    | Bool, OneZero | OneZero, Bool -> Bool
    | Int, OneZero | OneZero, Int -> Int
    | Float, OneZero | OneZero, Float -> Float
    | Float, Int | Int, Float -> Float
    | _ -> String

  let formatType = function
    | InferredType.Any | InferredType.String -> "string"
    | InferredType.Bool | InferredType.OneZero -> "bool"
    | InferredType.Int | InferredType.Float -> "number"
    | InferredType.Date _ -> "date"

let inferTypes headers rows = 
  rows
  |> Seq.map (Array.map Inference.inferType)
  |> Seq.reduce (Array.map2 Inference.unifyTypes)
  |> Seq.zip headers
  |> Array.ofSeq

(*
let readCsvFile (data:string) =   
  if String.IsNullOrWhiteSpace data then parseError "The specified input was empty."
  let firstNewLine = data.IndexOfAny [|'\n'; '\r' |]
  let firstLine = if firstNewLine > 0 then data.Substring(0, firstNewLine) else data
  let separators = 
    if firstLine.Contains("\t") then "\t"
    elif firstLine.Contains(";") then ";"
    else ","
  let file = try CsvFile.Parse(data,separators) with _ -> parseError "Failed to parse data as a CSV file."
  if Seq.isEmpty file.Rows then parseError "The specified CSV file contains no data."

  let meta = 
    file.Rows 
    |> Seq.truncate 100
    |> Seq.map (fun r -> r.Columns)
    |> inferTypes file.Headers.Value
  
  let data = 
    file.Rows
    |> Seq.map (fun row -> 
      Seq.zip meta row.Columns 
      |> Seq.map (fun ((col, typ), value) -> 
        try 
          match typ with 
          | Number -> col, Value.Number(Double.Parse value)
          | Date null -> col, Value.Date(DateTimeOffset.Parse(value, ddmm))
          | Date c -> col, Value.Date(DateTimeOffset.Parse(value, c))
          | OneZero
          | Bool -> 
              let b = 
                if value.ToLower() = "true" then true
                elif value.ToLower() = "false" then false
                else 
                  let d = Decimal.Parse(value)
                  if d = 1M then true elif d = 0M then false
                  else failwithf "%s is not a boolean" value
              col, Value.Bool b
          | Any | String -> col, Value.String(value)
        with _ -> parseError (sprintf "Column '%s' was inferred as %s, but contians non-numeric value '%s'." col (typeName typ) value) )
      |> Array.ofSeq )
    |> Array.ofSeq

  meta |> Seq.map (fun (n, ty) -> n, typeName ty) |> Array.ofSeq,
  data
  *)

// ------------------------------------------------------------------------------------------------
// Handling pivot queries in-memory
// ------------------------------------------------------------------------------------------------

open Evaluator
open Inference
open Serializer

let truncatePreview isPreview = 
  if isPreview then Array.truncate 10 else id 

let applyAction isPreview meta objs = function
  | GetSeries(k, v) ->
      objs 
      |> Seq.map (fun obj ->
        let kn, kval = Array.find (fst >> (=) k) obj
        let vn, vval = Array.find (fst >> (=) v) obj
        [| kn, kval; vn, vval |]) 
      |> Array.ofSeq 
      |> truncatePreview isPreview 
      |> serialize true
  | GetTheData -> 
      objs
      |> truncatePreview isPreview 
      |> serialize false
  | Metadata -> 
      JsonValue.Record [| for k, v in meta -> k, JsonValue.String (formatType v) |]
  | GetRange(fld) ->
      objs |> Array.map (pickField fld) |> Array.distinct |> Array.map serializeValue |> JsonValue.Array

let handleInMemoryRequest meta source query = 
  let source = source |> Seq.ofArray
  let preview, query = query |> List.partition ((=) "preview")
  let isPreview = not (List.isEmpty preview)
  let query = (match query with x::_ -> x | _ -> "") |> Transform.fromUrl
  let res = query.Transformations |> List.fold transformData source |> Array.ofSeq
  let json = applyAction isPreview meta res query.Action
  Successful.OK (json.ToString())  
 
// ------------------------------------------------------------------------------------------------
// 
// ------------------------------------------------------------------------------------------------

type SqlQuerySource = 
  | Table of string
  | Nested of SqlQuery

and SqlColumn = 
  | Literal of string
  | Column of string

and SqlQuery = 
  { Source : SqlQuerySource
    Select : SqlColumn list
    Paging : option<float * float>
    OrderBy : option<list<string * SortDirection * (string -> string)>> }

let nest query = 
  { Source = Nested query 
    Select = query.Select
    Paging = None
    OrderBy = None }

let formatName col = 
  if not (Seq.forall (fun c -> Char.IsLetterOrDigit(c) || c = '.' || c = '-' || c = '_') col) then
    failwithf "Invalid column or table name: %s" col
  "[" + col + "]"

let rec formatSqlQuery query = 
  let source = 
    match query.Source with 
    | Nested q -> "(" + formatSqlQuery q + ") t"
    | Table s -> formatName s
  "SELECT " + 
  ( String.concat ", " 
      [ for c in query.Select ->
          match c with Literal s -> s | Column c -> formatName c ]) +
  " FROM " + source + " " +
  ( match query.OrderBy with 
    | None -> ""
    | Some ords -> 
        "\nORDER BY " + String.concat "," 
          [ for c, o, conv in ords -> conv (formatName c) + " " + if o = Descending then "DESC " else "ASC " ] ) +
  ( match query.Paging with 
    | None -> ""
    | Some(skip, take) -> 
        ("\nOFFSET " + (string (int skip)) + " ROWS ") + 
        (if take <> infinity then "\nFETCH NEXT " + (string (int take)) + " ROWS ONLY" else "") )

let rec translateQuery meta tfs query = 
  match tfs with 
  | [] -> query
  | SortBy([])::tfs -> 
      translateQuery meta tfs query

  | SortBy(cols)::tfs -> 
      let query = 
        if query.Paging.IsNone then query
        else nest query
      let cols = 
        [ for c, o in cols ->
            match Map.tryFind c meta with 
            | Some (InferredType.String | InferredType.Any) -> c, o, (sprintf "CAST(%s AS nvarchar(1000))")
            | _ -> c, o, id ]
      translateQuery meta tfs { query with OrderBy = Some cols }

  | Paging(pgs)::tfs ->
      let query = 
        if query.OrderBy.IsSome then query 
        else { query with Select = query.Select @ [Literal("0 as [temp_sort]")]; OrderBy = Some ["temp_sort", Ascending, id] }
      let skip, take = defaultArg query.Paging (0., infinity) 
      let skip, take = pgs |> List.fold (fun (skip, take) pg -> 
        match pg with 
        | Skip n -> (skip + float n, take - float n)
        | Take n -> (skip, min (float n) take)) (skip, take)
      translateQuery meta tfs { query with Paging = Some (skip, take) }
  | _::tfs -> translateQuery meta tfs query
(*
let demo = 
  { Action = GetTheData 
    Transformations = 
      [ SortBy ["air_pollution_score", Ascending]
        Paging [Take 10; Skip 1; Take 20; Skip 1]
      ]}

translateQuery demo.Transformations   
  { Source = Table "enigma-us.gov.epa.nvfel.fuel-economy.green.2014-preview"
    Paging = None
    OrderBy = None }
|> formatSqlQuery

*)

let runQuery connStrSql table meta tfs = 
  let query = 
    translateQuery (Map.ofSeq meta) tfs 
      { Select = meta |> Array.map (fst >> Column) |> List.ofArray
        Source = Table table; Paging = None; OrderBy = None }
    |> formatSqlQuery 
  printfn "SQL: %s" query
  Database.executeReader connStrSql query
    (fun row -> meta |> Array.mapi (fun i (col, typ) ->
      let isNull = row.IsDBNull(i)
      match typ with
      | InferredType.Any | InferredType.String when isNull -> col, Value.String("")
      | _ when isNull -> failwith "Unexpected null value"
      | InferredType.Any | InferredType.String -> col, Value.String(row.GetString(i))
      | InferredType.Bool | InferredType.OneZero -> col, Value.Bool(row.GetBoolean(i))
      | InferredType.Date _ -> col, Value.Date(row.GetDateTimeOffset(i))
      | InferredType.Int -> col, Value.Number(float(row.GetInt32(i)))
      | InferredType.Float -> col, Value.Number(float(row.GetFloat(i))) ))   

let handleSqlRequest connStrSql table meta query = 
  //let source = source |> Seq.ofArray
  let preview, query = query |> List.partition ((=) "preview")
  let isPreview = not (List.isEmpty preview)
  let query = (match query with x::_ -> x | _ -> "") |> Transform.fromUrl
  let query = 
      if not isPreview then query
      else { query with Transformations = query.Transformations @ [Paging [Take 10]] }

  let json = 
    match query.Action with
    | Metadata -> 
        JsonValue.Record [| for k, v in meta -> k, JsonValue.String (formatType v) |]
    
    | GetTheData ->
        let res = runQuery connStrSql table meta query.Transformations
        res.ToArray() |> serialize false

    | _ -> JsonValue.Array [||]  



  (*| GetSeries(k, v) ->
    objs |> Seq.map (fun obj ->
      let kn, kval = Array.find (fst >> (=) k) obj
      let vn, vval = Array.find (fst >> (=) v) obj
      [| kn, kval; vn, vval |]) |> Array.ofSeq |> serialize isPreview true
  | GetTheData -> 
      objs |> serialize isPreview false
  | GetRange(fld) ->
      objs |> Array.map (pickField fld) |> Array.distinct |> Array.map serializeValue |> JsonValue.Array

  //let res = query.Transformations |> List.fold transformData source |> Array.ofSeq
  //let json = applyAction isPreview meta res query.Action
  *)
  Successful.OK (json.ToString())  
