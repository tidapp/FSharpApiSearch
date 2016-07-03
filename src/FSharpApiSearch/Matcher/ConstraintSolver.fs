﻿module internal FSharpApiSearch.ConstraintSolver

open System.Diagnostics
open FSharpApiSearch.MatcherTypes
open FSharpApiSearch.SpecialTypes

let getFullTypeDefinition (ctx: Context) (baseType: LowType) =
  let rec getIdentity = function
    | Identity i -> i
    | Generic (x, _) -> getIdentity x
    | Tuple xs -> Identity.tupleN xs.Length
    | TypeAbbreviation { Original = o } -> getIdentity o
    | _ -> failwith "invalid base type."
  match getIdentity baseType with
  | FullIdentity full ->
    ctx.ApiDictionaries.[full.AssemblyName].TypeDefinitions |> Seq.find (fun td -> Identity.testFullIdentity td.FullIdentity full)
    |> Array.singleton
  | PartialIdentity partial ->
    ctx.QueryTypes.[partial]

let rec (|ConstraintTestee|_|) = function
  | Identity id -> Some (id, [])
  | Generic (Identity id, args) -> Some (id, args)
  | Tuple xs -> Some (Identity.tupleN xs.Length, xs)
  | TypeAbbreviation { Original = o } -> (|ConstraintTestee|_|) o
  | _ -> None 

let createConstraintSolver title testConstraint (testeeType: LowType) ctx = seq {
  match testeeType with
  | ConstraintTestee (testeeIdentity, testTypeArgs) ->
    let testees =
      match testeeIdentity with
      | FullIdentity i -> ctx.ApiDictionaries.[i.AssemblyName].TypeDefinitions |> Seq.find (fun td -> Identity.testFullIdentity td.FullIdentity i) |> Array.singleton
      | PartialIdentity i -> ctx.QueryTypes.[i]
    for typeDef in testees do
      Debug.WriteLine(sprintf "Test %s: %s" title (typeDef.Debug()))
      Debug.Indent()
      let nextCtx =
        match testeeIdentity with
        | FullIdentity _ -> ctx
        | PartialIdentity i -> { ctx with QueryTypes = ctx.QueryTypes |> Map.add i [| typeDef |] }
      let results = testConstraint typeDef testTypeArgs nextCtx |> Seq.cache
      Debug.Unindent()
      Debug.WriteLine(
        if Seq.isEmpty results = false then
          sprintf "Success %s, %d branches." title (Seq.length results)
        else
          sprintf "Failure %s." title
      )
      yield! results
  | Variable _ -> yield ctx
  | Wildcard _ -> yield ctx
  | _ -> ()
}

let transferVariableArgument (inheritArgs: Map<TypeVariable, LowType>) (baseType: LowType): LowType list =
  let rec genericArguments = function
    | Identity _ -> []
    | Generic (_, args) -> args
    | TypeAbbreviation { Original = o } -> genericArguments o
    | _ -> failwith "invalid base type."
  genericArguments baseType
  |> List.map (function
    | Variable (VariableSource.Target, v) -> inheritArgs.[v]
    | a -> a)

let instantiate (t: FullTypeDefinition) (args: LowType list) =
  let id = Identity (FullIdentity t.FullIdentity)
  match args with
  | [] -> id
  | _ -> Generic (id, args)

let rec getInheritTypes (ctx: Context) (t: FullTypeDefinition) (args: LowType list): LowType seq = seq {
  let argPair = List.zip t.GenericParameters args |> Map.ofList

  let thisType = instantiate t args
  yield thisType 

  let parents = seq {
    match t.BaseType with
    | Some baseType -> yield baseType
    | None -> ()

    yield! t.AllInterfaces
  }

  for p in parents do
    let baseTypeArgs = transferVariableArgument argPair p
    let baseTypeDef =
      let rec getFullIdentity = function
        | Identity (FullIdentity full) -> full
        | Generic (Identity (FullIdentity full), _) -> full
        | TypeAbbreviation { Original = o } -> getFullIdentity o
        | _ -> failwith "It is not full identity."
      let full = getFullIdentity p
      ctx.ApiDictionaries.[full.AssemblyName].TypeDefinitions |> Seq.find (fun td -> td.FullIdentity = full)
    yield! getInheritTypes ctx baseTypeDef baseTypeArgs
}

let firstMatched f xs =
  xs
  |> Seq.tryPick (fun x -> match f x with Matched ctx -> Some ctx | _ -> None)
  |> function
    | Some ctx -> Seq.singleton ctx
    | None -> Seq.empty

let testSubtypeConstraint (lowTypeMatcher: ILowTypeMatcher) (parentType: LowType) =
  createConstraintSolver
    "subtype constrints"
    (fun testeeTypeDef testeeArgs ctx ->
      let testees =
        match parentType with
        | Variable _ -> Seq.singleton (instantiate testeeTypeDef testeeArgs)
        | _ -> getInheritTypes ctx testeeTypeDef testeeArgs
      testees
      |> firstMatched (fun t -> lowTypeMatcher.Test t parentType ctx)
    )

let addGenericMemberReplacements (m: Member) replacements =
  m.GenericParameters
  |> Seq.fold (fun replacements v ->
    replacements |> Map.add v (Wildcard None)
  ) replacements

let normalizeGetterMethod (m: Member) =
  let args =
    match m.Arguments with
    | [] -> [ LowType.Unit ]
    | args -> args
  Arrow [ yield! args; yield m.ReturnType ]

let normalizeSetterMethod (m: Member) =
  let args = [
    yield! m.Arguments
    yield m.ReturnType
  ]
  Arrow [ yield! args; yield LowType.Unit ]

let normalizeMethod (m: Member) =
  Arrow [ yield! m.Arguments; yield m.ReturnType ]

let testMemberConstraint (lowTypeMatcher: ILowTypeMatcher) modifier (expectedMember: Member) =
  let normalizedExpectedMember  =
    let xs = [ yield! expectedMember.Arguments; yield expectedMember.ReturnType ]
    Arrow xs

  createConstraintSolver
    "member constraints"
    (fun testeeTypeDef testeeArgs ctx ->
      Debug.WriteLine("Member normalize to arrow or function.")
      let members =
        match modifier with
        | MemberModifier.Static -> Seq.append testeeTypeDef.StaticMembers testeeTypeDef.ImplicitStaticMembers
        | MemberModifier.Instance -> Seq.append testeeTypeDef.InstanceMembers testeeTypeDef.ImplicitInstanceMembers
      let genericTypeReplacements = List.zip testeeTypeDef.GenericParameters testeeArgs |> Map.ofList
      members
      |> Seq.choose (fun member' ->
        let normalized =
          match member' with
          | { Kind = MemberKind.Property PropertyKind.Get } ->
            if "get_" + member'.Name = expectedMember.Name then Some (normalizeGetterMethod member') else None
          | { Kind = MemberKind.Property PropertyKind.Set } ->
            if "set_" + member'.Name = expectedMember.Name then Some (normalizeSetterMethod member') else None
          | { Kind = MemberKind.Property PropertyKind.GetSet } ->
            if "get_" + member'.Name = expectedMember.Name then Some (normalizeGetterMethod member')
            elif "set_" + member'.Name = expectedMember.Name then Some (normalizeSetterMethod member')
            else None
          | _->
            if member'.Name = expectedMember.Name && member'.IsCurried = false && member'.Arguments.Length = expectedMember.Arguments.Length then
              Some (normalizeMethod member')
            else None
        normalized |> Option.map (fun x -> (x, addGenericMemberReplacements member' genericTypeReplacements))
      )
      |> Seq.map (fun (x, replacements) -> LowType.applyVariable VariableSource.Target replacements x)
      |> firstMatched (fun x -> lowTypeMatcher.Test x normalizedExpectedMember ctx)
    )

let createConstraintStatusSolver name (get: _ -> ConstraintStatus) =
  let rec testConstraint testeeSignature ctx =
    let test =
      createConstraintSolver
        (sprintf "%s constraints" name)
        (fun testeeTypeDef testeeArgs ctx ->
          match get testeeTypeDef with
          | Satisfy -> Seq.singleton ctx
          | NotSatisfy -> Seq.empty
          | Dependence xs -> fold testeeTypeDef testeeArgs xs ctx)
    test testeeSignature ctx
  and fold (typeDef: FullTypeDefinition) args (dependentVariables: TypeVariable list) ctx =
    let testArgs =
      typeDef.GenericParameters
      |> List.map (fun p -> List.exists ((=)p) dependentVariables)
      |> List.zip args
      |> List.choose (fun (arg, isDependent) -> if isDependent then Some arg else None)
    Debug.WriteLine(sprintf "Test %s of dependent types: %A" name (testArgs |> List.map (fun x -> x.Debug())))
    let branches =
      testArgs
      |> Seq.fold (fun contextBranches testeeSignature ->
        seq {
          for ctxBranch in contextBranches do
            yield! testConstraint testeeSignature ctxBranch
        }
      ) (Seq.singleton ctx)
      |> Seq.cache
    Debug.WriteLine(sprintf "%d branches from dependent types." (Seq.length branches))
    branches
  testConstraint

let testNullnessConstraint = createConstraintStatusSolver "nullness" (fun x -> x.SupportNull)
let testDefaultConstructorConstriant = createConstraintStatusSolver "default constructor" (fun x -> x.DefaultConstructor)
let testValueTypeConstraint = createConstraintStatusSolver "value type" (fun x -> x.ValueType)
let testReferenceTypeConstraint = createConstraintStatusSolver "reference type" (fun x -> x.ReferenceType)
let testEqualityConstraint = createConstraintStatusSolver "equality" (fun x -> x.Equality)
let testComparisonConstraint = createConstraintStatusSolver "comparison" (fun x -> x.Comparison)

let rec solve' (lowTypeMatcher: ILowTypeMatcher) (constraints: TypeConstraint list) (initialCtx: Context) (testEqualities: (LowType * LowType) list) =
  let getTestSignatures variable =
    let variable = Variable (VariableSource.Target, variable)
    testEqualities |> List.choose (fun (left, right) -> if left = variable then Some right elif right = variable then Some left else None)
    
  Debug.WriteLine("Begin solving type constraints.")
  Debug.WriteLine(sprintf "Equalities: %A" (List.map Equations.debugEquality testEqualities))
  Debug.WriteLine(sprintf "Constraints: %A" (constraints |> List.map TypeConstraint.debug))
  Debug.Indent()

  let testConstraint constraint' contextBranches testeeSignature: Context seq = seq {
    let inline pass ctx = Seq.singleton ctx
    for ctx in contextBranches do
      match constraint' with
      | SubtypeConstraints parentType ->
        yield! testSubtypeConstraint lowTypeMatcher parentType testeeSignature ctx
      | NullnessConstraints ->
        yield! testNullnessConstraint testeeSignature ctx
      | MemberConstraints (modifier, member') ->
        yield! testMemberConstraint lowTypeMatcher modifier member' testeeSignature ctx
      | DefaultConstructorConstraints ->
        yield! testDefaultConstructorConstriant testeeSignature ctx
      | ValueTypeConstraints ->
        yield! testValueTypeConstraint testeeSignature ctx
      | ReferenceTypeConstraints ->
        yield! testReferenceTypeConstraint testeeSignature ctx
      | EnumerationConstraints ->
        yield! pass ctx
      | DelegateConstraints ->
        yield! pass ctx
      | UnmanagedConstraints ->
        yield! pass ctx
      | EqualityConstraints ->
        yield! testEqualityConstraint testeeSignature ctx
      | ComparisonConstraints ->
        yield! testComparisonConstraint testeeSignature ctx
  }
  let result =
    constraints
    |> Seq.fold (fun contextBranches constraint' ->
      seq {
        for variable in constraint'.Variables do
          Debug.WriteLine(sprintf "Constraint test: %s" (constraint'.Debug()))
          let testSignatures = getTestSignatures variable
          Debug.WriteLine(sprintf "Constraint test signatures: %A" (testSignatures |> List.map (fun x -> x.Debug())))
          yield! testSignatures |> List.fold (testConstraint constraint'.Constraint) contextBranches
      }
    ) (Seq.singleton initialCtx)
    |> Seq.tryHead
    |> function
      | Some ctx ->
        match ctx.Equations.Equalities |> List.take (ctx.Equations.Equalities.Length - initialCtx.Equations.Equalities.Length) with
        | [] -> Matched ctx
        | newEqualities ->
          Debug.WriteLine(sprintf "There are new equalities." )
          Debug.Indent()
          let result = solve' lowTypeMatcher constraints ctx newEqualities
          Debug.Unindent()
          result
      | None -> Failure
        
  Debug.Unindent()
  Debug.WriteLine(sprintf "End solving type constraints. Result=%b" (MatchingResult.toBool result))
  result

let solve lowTypeMatcher constraints ctx = solve' lowTypeMatcher constraints ctx ctx.Equations.Equalities

let instance (_: SearchOptions) =
  { new IApiMatcher with
      member this.Name = "Constraint Solver"
      member this.Test lowTypeMatcher query api ctx = solve lowTypeMatcher api.TypeConstraints ctx }