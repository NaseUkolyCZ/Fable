[<CompilationRepresentation(CompilationRepresentationFlags.ModuleSuffix)>]
module Fabel.FSharp2Fabel

open Microsoft.FSharp.Compiler
open Microsoft.FSharp.Compiler.Ast
open Microsoft.FSharp.Compiler.SourceCodeServices
open Fabel.AST
open Fabel.FSharp2Fabel.Util

let rec private transformExpr com ctx fsExpr =
    match fsExpr with
    (** ## Erased *)
    | BasicPatterns.Coerce(_targetType, Transform com ctx inpExpr) -> inpExpr
    | BasicPatterns.NewDelegate(_delegateType, Transform com ctx delegateBodyExpr) -> delegateBodyExpr
    // TypeLambda is a local generic lambda
    // e.g, member x.Test() = let typeLambda x = x in typeLambda 1, typeLambda "A"
    | BasicPatterns.TypeLambda (_genArgs, Transform com ctx lambda) -> lambda

    | BasicPatterns.ILAsm (_asmCode, _typeArgs, argExprs) ->
        printfn "ILAsm detected in %A: %A" fsExpr.Range fsExpr // TODO: Check
        match argExprs with
        | [] -> Fabel.Value Fabel.Null |> makeExpr com ctx fsExpr
        | [Transform com ctx expr] -> expr
        | exprs -> Fabel.Sequential (List.map (transformExpr com ctx) exprs)
                    |> makeExpr com ctx fsExpr

    (** ## Flow control *)
    | BasicPatterns.FastIntegerForLoop(Transform com ctx start, Transform com ctx limit, body, isUp) ->
        match body with
        | BasicPatterns.Lambda (BindIdent com ctx (newContext, ident), body) ->
            Fabel.For (ident, start, limit, com.Transform newContext body, isUp)
            |> Fabel.Loop |> makeExpr com ctx fsExpr
        | _ -> failwithf "Unexpected loop in %A: %A" fsExpr.Range fsExpr

    | BasicPatterns.WhileLoop(Transform com ctx guardExpr, Transform com ctx bodyExpr) ->
        Fabel.While (guardExpr, bodyExpr)
        |> Fabel.Loop |> makeExpr com ctx fsExpr

    // This must appear before BasicPatterns.Let
    | ForOf (BindIdent com ctx (newContext, ident), Transform com ctx value, body) ->
        Fabel.ForOf (ident, value, transformExpr com newContext body)
        |> Fabel.Loop |> makeExpr com ctx fsExpr

    (** Values *)
    | BasicPatterns.Const(value, _typ) ->
        makeExpr com ctx fsExpr (makeConst value)

    | BasicPatterns.BaseValue _type ->
        makeExpr com ctx fsExpr (Fabel.Value Fabel.Super)

    | BasicPatterns.ThisValue _type ->
        makeExpr com ctx fsExpr (Fabel.Value Fabel.This)

    | BasicPatterns.Value thisVar when thisVar.IsMemberThisValue ->
        makeExpr com ctx fsExpr (Fabel.Value Fabel.This)

    | BasicPatterns.Value(GetIdent com ctx ident) ->
        upcast ident

    | BasicPatterns.DefaultValue (FabelType com typ) ->
        let valueKind =
            match typ with
            | Fabel.PrimitiveType Fabel.Boolean -> Fabel.BoolConst false
            | Fabel.PrimitiveType (Fabel.Number _) -> Fabel.IntConst 0
            | _ -> Fabel.Null
        makeExpr com ctx fsExpr (Fabel.Value valueKind)

    (** ## Assignments *)
    // TODO: Possible optimization if binding to another ident (let x = y), just replace it in the ctx
    | BasicPatterns.Let((BindIdent com ctx (newContext, ident) as var,
                            Transform com ctx value), body) ->
        let body = transformExpr com newContext body
        let assignment = Fabel.VarDeclaration (ident, value, var.IsMutable) |> makeExpr com ctx fsExpr
        match body.Kind with
        // Check if this this is just a wrapper to a call as it happens in pipelines
        // e.g., let x = 5 in fun y -> methodCall x y
        | Fabel.Lambda (lambdaArgs,
                        (ExprKind (Fabel.Apply (eBody, ReplaceArgs [ident,value] args, isCons)) as e),
                        Fabel.Immediate, false) ->
            Fabel.Lambda (lambdaArgs,
                Fabel.Expr (Fabel.Apply (eBody, args, isCons), e.Type, ?range=e.Range),
                Fabel.Immediate, false) |> makeExpr com ctx fsExpr
        | Fabel.Sequential statements ->
            makeSequential (assignment::statements)
        | _ -> makeSequential [assignment; body]

    | BasicPatterns.LetRec(recursiveBindings, body) ->
        let newContext, idents =
            recursiveBindings
            |> Seq.foldBack (fun (var, _) (accContext, accIdents) ->
                let (BindIdent com accContext (newContext, ident)) = var
                newContext, (ident::accIdents)) <| (ctx, [])
        let assignments =
            recursiveBindings
            |> List.map2 (fun ident (var, Transform com ctx binding) ->
                Fabel.VarDeclaration (ident, binding, var.IsMutable)
                |> makeExpr com ctx fsExpr) idents
        match transformExpr com newContext body with
        | ExprKind (Fabel.Sequential statements) -> assignments @ statements
        | _ as body -> assignments @ [ body ]
        |> makeSequential

    (** ## Applications *)
    | BasicPatterns.TraitCall (_sourceTypes, traitName, _typeArgs, _typeInstantiation, argExprs) ->
        printfn "TraitCall detected in %A: %A" fsExpr.Range fsExpr // TODO: Check
        makeGetApply (transformExpr com ctx argExprs.Head) traitName
                     (List.map (transformExpr com ctx) argExprs.Tail)
        |> makeExpr com ctx fsExpr

    // TODO: Check `inline` annotation?
    // TODO: Watch for restParam attribute
    | BasicPatterns.Call(callee, meth, _typeArgs1, _typeArgs2, args) ->
        makeCall com ctx fsExpr callee meth args

    | BasicPatterns.Application(Transform com ctx expr, _typeArgs, args) ->
        makeApply ctx expr (List.map (transformExpr com ctx) args)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.IfThenElse (Transform com ctx guardExpr, Transform com ctx thenExpr, Transform com ctx elseExpr) ->
        Fabel.IfThenElse (guardExpr, thenExpr, elseExpr)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.TryFinally (BasicPatterns.TryWith(body, _, _, catchVar, catchBody),finalBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) (Some finalBody)

    | BasicPatterns.TryFinally (body, finalBody) ->
        makeTryCatch com ctx fsExpr body None (Some finalBody)

    | BasicPatterns.TryWith (body, _, _, catchVar, catchBody) ->
        makeTryCatch com ctx fsExpr body (Some (catchVar, catchBody)) None

    | BasicPatterns.Sequential (Transform com ctx first, Transform com ctx second) ->
        match first.Kind with
        | Fabel.Value (Fabel.Null) -> second
        | _ ->
            match second.Kind with
            | Fabel.Sequential statements -> first::statements
            | _ -> [first; second]
            |> makeSequential

    (** ## Lambdas *)
    | BasicPatterns.Lambda (var, body) ->
        makeLambda com ctx (Some fsExpr.Range) [var] body

    (** ## Getters and Setters *)
    | BasicPatterns.ILFieldGet (callee, typ, fieldName) ->
        failwithf "Found unsupported ILField reference in %A: %A" fsExpr.Range fsExpr

    // TODO: Check if it's FSharpException
    // TODO: Change name of automatically generated fields
    | BasicPatterns.FSharpFieldGet (callee, FabelType com calleeType, FieldName fieldName) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeTypeRef calleeType
        Fabel.Get (callee, makeLiteral fieldName)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.TupleGet (_tupleType, tupleElemIndex, Transform com ctx tupleExpr) ->
        Fabel.Get (tupleExpr, makeLiteral tupleElemIndex)
        |> makeExpr com ctx fsExpr

    // Single field: Item; Multiple fields: Item1, Item2...
    | BasicPatterns.UnionCaseGet (Transform com ctx unionExpr, FabelType com unionType, unionCase, FieldName fieldName) ->
        match unionType with
        | ErasedUnion | OptionUnion -> unionExpr
        | ListUnion -> failwith "TODO"
        | OtherType ->
            Fabel.Get (unionExpr, makeLiteral fieldName)
            |> makeExpr com ctx fsExpr

    | BasicPatterns.ILFieldSet (callee, typ, fieldName, value) ->
        failwithf "Found unsupported ILField reference in %A: %A" fsExpr.Range fsExpr

    // TODO: Change name of automatically generated fields
    | BasicPatterns.FSharpFieldSet (callee, FabelType com calleeType, FieldName fieldName, Transform com ctx value) ->
        let callee =
            match callee with
            | Some (Transform com ctx callee) -> callee
            | None -> makeTypeRef calleeType
        Fabel.Set (callee, Some (makeLiteral fieldName), value)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.UnionCaseTag (Transform com ctx unionExpr, _unionType) ->
        Fabel.Get (unionExpr, makeLiteral "Tag")
        |> makeExpr com ctx fsExpr

    // We don't need to check if this an erased union, as union case values are only set
    // in constructors, which are ignored for erased unions
    | BasicPatterns.UnionCaseSet (Transform com ctx unionExpr, _type, _case, FieldName caseField, Transform com ctx valueExpr) ->
        Fabel.Set (unionExpr, Some (makeLiteral caseField), valueExpr)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.ValueSet (GetIdent com ctx valToSet, Transform com ctx valueExpr) ->
        Fabel.Set (valToSet, None, valueExpr)
        |> makeExpr com ctx fsExpr

    (** Instantiation *)
    | BasicPatterns.NewArray(FabelType com typ, argExprs) ->
        match typ with
        | Fabel.PrimitiveType (Fabel.TypedArray numberKind) -> failwith "TODO: NewArray args"
        | _ -> Fabel.Value (Fabel.ArrayConst (argExprs |> List.map (transformExpr com ctx)))
        |> makeExpr com ctx fsExpr

    | BasicPatterns.NewTuple(_, argExprs) ->
        Fabel.Value (Fabel.ArrayConst (argExprs |> List.map (transformExpr com ctx)))
        |> makeExpr com ctx fsExpr

    | BasicPatterns.ObjectExpr(_objType, _baseCallExpr, _overrides, interfaceImplementations) ->
        failwith "TODO"

    // TODO: Check for erased constructors with property assignment (Call + Sequential)
    | BasicPatterns.NewObject(meth, _typeArgs, args) ->
        makeCall com ctx fsExpr None meth args

    // TODO: Check if it's FSharpException
    // TODO: Create constructors for Record and Union types
    | BasicPatterns.NewRecord(FabelType com recordType, argExprs) ->
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        Fabel.Apply (makeTypeRef recordType, argExprs, true)
        |> makeExpr com ctx fsExpr

    | BasicPatterns.NewUnionCase(FabelType com unionType, unionCase, argExprs) ->
        let argExprs = argExprs |> List.map (transformExpr com ctx)
        match unionType with
        | ErasedUnion | OptionUnion ->
            match argExprs with
            | [] -> Fabel.Value Fabel.Null |> makeExpr com ctx fsExpr
            | [expr] -> expr
            | _ -> failwithf "Erased Union Cases must have one single field: %A" unionType
        | ListUnion ->
            match unionCase.Name with
            | "Cons" -> Fabel.Apply (Fabel.Value (Fabel.CoreModule "List") |> Fabel.Expr,
                            (makeLiteral "Cons")::argExprs, true)
            | _ -> Fabel.Value Fabel.Null
            |> makeExpr com ctx fsExpr
        | OtherType ->
            // Include Tag name in args
            let argExprs = (makeLiteral unionCase.Name)::argExprs
            Fabel.Apply (makeTypeRef unionType, argExprs, true)
            |> makeExpr com ctx fsExpr

    (** ## Type test *)
    | BasicPatterns.TypeTest (FabelType com typ as fsTyp, Transform com ctx expr) ->
        makeTypeTest typ expr |> makeExpr com ctx fsExpr

    | BasicPatterns.UnionCaseTest (Transform com ctx unionExpr, FabelType com unionType, unionCase) ->
        match unionType with
        | ErasedUnion ->
            if unionCase.UnionCaseFields.Count <> 1 then
                failwithf "Erased Union Cases must have one single field: %A" unionType
            else
                let typ = makeType com unionCase.UnionCaseFields.[0].FieldType
                makeTypeTest typ unionExpr
        | OptionUnion | ListUnion ->
            let op = if (unionCase.Name = "None" || unionCase.Name = "Empty") then BinaryEqual else BinaryUnequal
            Fabel.Binary (op, unionExpr, Fabel.Value Fabel.Null |> Fabel.Expr)
            |> Fabel.Operation
        | OtherType ->
            Fabel.Binary (BinaryEqualStrict,
                Fabel.Get (unionExpr, makeLiteral "Tag") |> Fabel.Expr,
                makeLiteral unionCase.Name) |> Fabel.Operation
        |> makeExpr com ctx fsExpr

    (** Pattern Matching *)
    | BasicPatterns.DecisionTreeSuccess (decIndex, decBindings) ->
        match Map.tryFind decIndex ctx.decisionTargets with
        | None -> failwith "Missing decision target"
        // If we get a reference to a function, call it
        | Some (TargetRef targetRef) ->
            Fabel.Apply (targetRef, (decBindings |> List.map (transformExpr com ctx)), false)
            |> makeExpr com ctx fsExpr
        // If we get an implementation without bindings, just transform it
        | Some (TargetImpl ([], Transform com ctx decBody)) -> decBody
        // If we have bindings, create the assignments
        | Some (TargetImpl (decVars, decBody)) ->
            let newContext, assignments =
                List.foldBack2 (fun var (Transform com ctx binding) (accContext, accAssignments) ->
                    let (BindIdent com accContext (newContext, ident)) = var
                    let assignment = Fabel.Expr (Fabel.VarDeclaration (ident, binding, var.IsMutable))
                    newContext, (assignment::accAssignments)) decVars decBindings (ctx, [])
            match transformExpr com newContext decBody with
            | ExprKind (Fabel.Sequential statements) -> assignments @ statements
            | _ as decBody -> assignments @ [ decBody ]
            |> makeSequential

    | BasicPatterns.DecisionTree(decisionExpr, decisionTargets) ->
        let rec getTargetRefsCount map = function
            | BasicPatterns.IfThenElse (_, thenExpr, elseExpr) ->
                let map = getTargetRefsCount map thenExpr
                getTargetRefsCount map elseExpr
            | BasicPatterns.DecisionTreeSuccess (idx, _) ->
                match (Map.tryFind idx map) with
                | Some refCount -> Map.remove idx map |> Map.add idx (refCount + 1)
                | None -> Map.add idx 1 map
            | _ as e ->
                failwithf "Unexpected DecisionTree branch in %A: %A" e.Range e
        let targetRefsCount = getTargetRefsCount (Map.empty<int,int>) decisionExpr
        // Convert targets referred more than once into functions
        // and just pass the F# implementation for the others
        let assignments =
            targetRefsCount
            |> Map.filter (fun k v -> v > 1)
            |> Map.fold (fun acc k v ->
                let decTargetVars, decTargetExpr = List.item k decisionTargets
                let lambda = makeLambda com ctx None decTargetVars decTargetExpr
                let ident = makeSanitizedIdent ctx lambda.Type (sprintf "$target%i" k)
                Map.add k (ident, lambda) acc) (Map.empty<_,_>)
        let decisionTargets =
            targetRefsCount |> Map.map (fun k v ->
                match v with
                | 1 -> TargetImpl (List.item k decisionTargets)
                | _ -> TargetRef (fst assignments.[k]))
        let newContext = { ctx with decisionTargets = decisionTargets }
        if assignments.Count = 0 then
            transformExpr com newContext decisionExpr
        else
            let assignments =
                assignments
                |> Seq.map (fun pair -> pair.Value)
                |> Seq.map (fun (ident, lambda) -> Fabel.VarDeclaration (ident, lambda, false))
                |> Seq.map Fabel.Expr
                |> Seq.toList
            Fabel.Sequential (assignments @ [transformExpr com newContext decisionExpr])
            |> makeExpr com ctx fsExpr

    (** Not implemented *)
    | BasicPatterns.Quote _ // (quotedExpr)
    | BasicPatterns.AddressOf _ // (lvalueExpr)
    | BasicPatterns.AddressSet _ // (lvalueExpr, rvalueExpr)
    | _ -> failwithf "Cannot compile expression in %A: %A" fsExpr.Range fsExpr

/// Declarations are returned in reverse order
let rec private transformDeclarations (com: IFabelCompiler) decls: Fabel.Declaration list =
    let emptyContext =
        { scope = []; decisionTargets = Map.empty<_,_>; }
    decls |> List.fold (fun (decls, lastChild: Fabel.Entity option, lastChildDecls) decl ->
        match decl with
        | FSharpImplementationFileDeclaration.Entity (e, sub) ->
            // TODO: Check if entity must be ignored because Import or Erase decorators
            let decls =
                match lastChild with
                | None -> decls
                | Some lastChild -> (Fabel.EntityDeclaration (lastChild, List.rev lastChildDecls))::decls
            let lastChild, lastChildDecls = com.GetEntity e, transformDeclarations com sub
            decls, Some lastChild, lastChildDecls
        | FSharpImplementationFileDeclaration.MemberOrFunctionOrValue (meth, args, body) ->
            let memberKind =
                // TODO: Check overloads, sanitize name (but tests)
                let name = meth.DisplayName
                if meth.IsImplicitConstructor then Fabel.Constructor
                elif meth.IsPropertyGetterMethod then Fabel.Getter name
                elif meth.IsPropertySetterMethod then Fabel.Setter name
                else Fabel.Method name
            let ctx, args =
                let args = if meth.IsInstanceMember then List.skip 1 args else args
                match args with
                | [] -> emptyContext, []
                | [[singleArg]] ->
                    makeType com singleArg.FullType |> function
                    | Fabel.PrimitiveType Fabel.Unit -> emptyContext, []
                    | _ -> let (BindIdent com emptyContext (ctx, arg)) = singleArg in ctx, [arg]
                | _ ->
                    Seq.foldBack (fun tupledArg (accContext, accArgs) ->
                        match tupledArg with
                        | [] -> failwith "Unexpected empty tupled in curried arguments"
                        | [nonTupledArg] ->
                            let (BindIdent com accContext (newContext, arg)) = nonTupledArg
                            newContext, arg::accArgs
                        | _ ->
                            // The F# compiler "untuples" the args in methods
                            let newContext, untupledArg = makeLambdaArgs com emptyContext tupledArg
                            newContext, untupledArg@accArgs
                    ) args (emptyContext, [])
            let entMember = 
                Fabel.Member(memberKind,
                    Fabel.LambdaExpr (args, transformExpr com ctx body, Fabel.Immediate, hasRestParams meth),
                    meth.Attributes |> Seq.choose (makeDecorator com) |> Seq.toList,
                    meth.Accessibility.IsPublic, not meth.IsInstanceMember)
                |> Fabel.MemberDeclaration
            // The F# compiler considers class methods as children of the enclosing module, correct that
            let methParentFullName = sanitizeEntityName meth.EnclosingEntity.FullName
            match lastChild with
            | Some x when x.FullName = methParentFullName -> decls, lastChild, entMember::lastChildDecls
            | _  -> entMember::decls, lastChild, lastChildDecls
        | FSharpImplementationFileDeclaration.InitAction (Transform com emptyContext expr) ->
            Fabel.ActionDeclaration expr::decls, lastChild, lastChildDecls
    ) ([], None, [])
    |> function
    | decls, None, _ -> decls
    | decls, Some lastChild, lastChildDecls ->
        (Fabel.EntityDeclaration (lastChild, List.rev lastChildDecls))::decls

let private makeCompiler (com: ICompiler) (fsProj: FSharpCheckProjectResults) =
    let entities =
        System.Collections.Concurrent.ConcurrentDictionary<string, Fabel.Entity>()
    let fileNames =
        fsProj.AssemblyContents.ImplementationFiles
        |> List.map (fun x -> x.FileName)
    let importedModules =
        fsProj.AssemblySignature.Entities
        |> Seq.fold (fun acc ent ->
            let isImport = false // TODO: Check import attribute
            if isImport then ent.FullName::acc else acc) []
    { new IFabelCompiler with
        member fcom.Transform ctx fsExpr =
            transformExpr fcom ctx fsExpr
        member fcom.IsInternal tdef =
            List.exists ((=) tdef.DeclarationLocation.FileName) fileNames
        member fcom.GetEntity tdef =
            entities.GetOrAdd (tdef.FullName, fun _ -> makeEntity fcom tdef)
        member fcom.GetSource tdef =
            importedModules
            |> List.tryPick (fun import ->
                if tdef.FullName.StartsWith import
                then Some (import, tdef.FullName)
                else None)
            |> function
            | Some (import, fullName) ->
                Fabel.Imported (import,
                    let route = fullName.Replace(import, "")
                    if route.StartsWith "."
                    then route.Substring(1)
                    else route)
            | None ->
                fileNames
                |> List.tryFind ((=) tdef.DeclarationLocation.FileName)
                |> function
                // TODO: Check Import attribute is not used just in case?
                | Some file -> Fabel.Internal file
                | None -> Fabel.External
      interface ICompiler with
        member __.Options = com.Options }

let transformFiles (com: ICompiler) (fsProj: FSharpCheckProjectResults) =
    // Consider an entity not empty when it contains:
    // 1) two or more not empty module declarations
    // 2) one or more non-module declarations
    // TODO: Another condition: contains a test suite declaration (module with TestSuite decorator)
    let rec getRootEntity (ent: Fabel.Entity) (decls: Fabel.Declaration list) =
        let rec isNotEmpty (decls: Fabel.Declaration list) =
            decls |> List.fold (fun (partialCondition, fullCondition) decl ->
                match fullCondition, decl with
                | true, _ -> true, true
                | false, Fabel.ActionDeclaration _
                | false, Fabel.MemberDeclaration _ -> true, true
                | false, Fabel.EntityDeclaration (e, _) when e.Kind <> Fabel.Module -> true, true
                | false, Fabel.EntityDeclaration (e, decls) ->
                    if isNotEmpty decls
                    then true, partialCondition
                    else partialCondition, false) (false, false)
            |> function _, fullCondition -> fullCondition
        if isNotEmpty decls then Some (ent, decls)
        else decls |> List.tryPick (function
            | Fabel.EntityDeclaration (e, decls) -> getRootEntity e decls
            | _ -> None)
    let com = makeCompiler com fsProj
    fsProj.AssemblyContents.ImplementationFiles |> List.choose (fun file ->
        let fileDecls = transformDeclarations com file.Declarations |> List.rev
        let fileEnt = Fabel.Entity (Fabel.Module, "global", [], [], true, Fabel.Internal file.FileName)
        // To prevent excessive nesting in JS modules, find the first non-empty module in the file
        match getRootEntity fileEnt fileDecls with
        | Some (rootEnt, rootDecls) -> Some(Fabel.File (file.FileName, rootEnt, rootDecls))
         // Skip empty files (e.g., those containing only Import or Erase types)
        | None -> None)