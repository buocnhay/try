﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Microsoft.DotNet.Interactive.FSharp

open System
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open FSharp.Compiler.Interactive.Shell
open FSharp.Compiler.Scripting
open FSharp.Compiler.SourceCodeServices
open FSharp.DependencyManager
open Microsoft.CodeAnalysis.Tags
open Microsoft.DotNet.Interactive
open Microsoft.DotNet.Interactive.Commands
open Microsoft.DotNet.Interactive.Events
open Microsoft.DotNet.Interactive.Utility

type FSharpKernel() =
    inherit KernelBase(Name = "fsharp")

    let resolvedAssemblies = List<string>()
    let script = new FSharpScript(additionalArgs=[|"/langversion:preview"|])
    let mutable cancellationTokenSource = new CancellationTokenSource()

    do Event.add resolvedAssemblies.Add script.AssemblyReferenceAdded
    do base.RegisterForDisposal(script)

    let messageMap = Dictionary<string, string>()

    let parseReference text =
        let reference, binLogPath = FSharpDependencyManager.parsePackageReference [text]
        (reference |> List.tryHead), binLogPath

    let packageInstallingMessages (refSpec: PackageReference option * string option option) =
        let ref, binLogPath = refSpec
        let versionText =
            match ref with
            | Some ref when ref.Version <> "*" -> ", version " + ref.Version
            | _ -> ""

        let installingMessage ref = "Installing package " + ref.Include + versionText + "."
        let loggingMessage = "Binary Logging enabled"
        [|
            match ref, binLogPath with
            | Some reference, Some _ ->
                yield installingMessage reference
                yield loggingMessage
            | Some reference, None ->
                yield installingMessage reference
            | None, Some _ ->
                yield loggingMessage
            | None, None ->
                ()
        |]

    let getLineAndColumn (text: string) offset =
        let rec getLineAndColumn' i l c =
            if i >= offset then l, c
            else
                match text.[i] with
                | '\n' -> getLineAndColumn' (i + 1) (l + 1) 0
                | _ -> getLineAndColumn' (i + 1) l (c + 1)
        getLineAndColumn' 0 1 0

    let kindString (glyph: FSharpGlyph) =
        match glyph with
        | FSharpGlyph.Class -> WellKnownTags.Class
        | FSharpGlyph.Constant -> WellKnownTags.Constant
        | FSharpGlyph.Delegate -> WellKnownTags.Delegate
        | FSharpGlyph.Enum -> WellKnownTags.Enum
        | FSharpGlyph.EnumMember -> WellKnownTags.EnumMember
        | FSharpGlyph.Event -> WellKnownTags.Event
        | FSharpGlyph.Exception -> WellKnownTags.Class
        | FSharpGlyph.Field -> WellKnownTags.Field
        | FSharpGlyph.Interface -> WellKnownTags.Interface
        | FSharpGlyph.Method -> WellKnownTags.Method
        | FSharpGlyph.OverridenMethod -> WellKnownTags.Method
        | FSharpGlyph.Module -> WellKnownTags.Module
        | FSharpGlyph.NameSpace -> WellKnownTags.Namespace
        | FSharpGlyph.Property -> WellKnownTags.Property
        | FSharpGlyph.Struct -> WellKnownTags.Structure
        | FSharpGlyph.Typedef -> WellKnownTags.Class
        | FSharpGlyph.Type -> WellKnownTags.Class
        | FSharpGlyph.Union -> WellKnownTags.Enum
        | FSharpGlyph.Variable -> WellKnownTags.Local
        | FSharpGlyph.ExtensionMethod -> WellKnownTags.ExtensionMethod
        | FSharpGlyph.Error -> WellKnownTags.Error

    let filterText (declarationItem: FSharpDeclarationListItem) =
        match declarationItem.NamespaceToOpen, declarationItem.Name.Split '.' with
        // There is no namespace to open and the item name does not contain dots, so we don't need to pass special FilterText to Roslyn.
        | None, [|_|] -> null
        // Either we have a namespace to open ("DateTime (open System)") or item name contains dots ("Array.map"), or both.
        // We are passing last part of long ident as FilterText.
        | _, idents -> Array.last idents

    let documentation (declarationItem: FSharpDeclarationListItem) =
        declarationItem.DescriptionText.ToString()

    let completionItem (declarationItem: FSharpDeclarationListItem) =
        let kind = kindString declarationItem.Glyph
        let filterText = filterText declarationItem
        let documentation = documentation declarationItem
        CompletionItem(declarationItem.Name, kind, filterText=filterText, documentation=documentation)

    let handleSubmitCode (codeSubmission: SubmitCode) (context: KernelInvocationContext) =
        async {

            script.DependencyAdding
            |> Event.add (fun (key, referenceText) ->
                if key = "nuget" then
                    let reference = parseReference referenceText
                    for message in packageInstallingMessages reference do
                        let key = message
                        messageMap.[key] <- message
                        context.Publish(DisplayedValueProduced(message, context.Command, valueId=key))
                ())

            script.DependencyAdded
            |> Event.add (fun (key, referenceText) ->
                if key = "nuget" then
                    let reference = parseReference referenceText
                    match reference with
                    | Some ref, _ ->
                        let packageRef =
                            if ref.Version = "*" then
                                NugetPackageReference(ref.Include)
                            else
                                NugetPackageReference(ref.Include, packageVersion=ref.Version)
                        context.Publish(NuGetPackageAdded(AddNugetPackage(packageRef), packageRef))
                    | _ -> ()

                    for key in packageInstallingMessages reference do
                        let message = messageMap.[key] + "done!"
                        context.Publish(DisplayedValueUpdated(message, key))
                    ())

            script.DependencyFailed
            |> Event.add (fun (key, referenceText) ->
                if key = "nuget" then
                    let reference = parseReference referenceText
                    for key in packageInstallingMessages reference do
                        let message = messageMap.[key] + "failed!"
                        context.Publish(DisplayedValueUpdated(message, key))
                    ())

            let codeSubmissionReceived = CodeSubmissionReceived(codeSubmission.Code, codeSubmission)
            context.Publish(codeSubmissionReceived)
            use! console = ConsoleOutput.Capture() |> Async.AwaitTask
            use _ = console.SubscribeToStandardOutput(fun msg -> context.Publish(StandardOutputValueProduced(msg, codeSubmission, FormattedValue.FromObject(msg))))
            use _ = console.SubscribeToStandardError(fun msg -> context.Publish(StandardErrorValueProduced(msg, codeSubmission, FormattedValue.FromObject(msg))))
            resolvedAssemblies.Clear()
            let tokenSource = cancellationTokenSource
            let result, errors =
                try
                    script.Eval(codeSubmission.Code, tokenSource.Token)
                with
                | ex -> Error(ex), [||]

            match result with
            | Ok(result) ->
                match result with
                | Some(value) ->
                    let value = value.ReflectionValue
                    let formattedValues = FormattedValue.FromObject(value)
                    context.Publish(ReturnValueProduced(value, codeSubmission, formattedValues))
                | None -> ()
                context.Complete()
            | Error(ex) ->
                if not (tokenSource.IsCancellationRequested) then
                    let aggregateError = String.Join("\n", errors)
                    let reportedException =
                        match ex with
                        | :? FsiCompilationException -> CodeSubmissionCompilationErrorException(ex) :> Exception
                        | _ -> ex
                    context.Publish(CommandFailed(reportedException, codeSubmission, aggregateError))
                else
                    context.Publish(new CommandFailed(null, codeSubmission, "Command cancelled"))
        }

    let handleRequestCompletion (requestCompletion: RequestCompletion) (context: KernelInvocationContext) =
        async {
            context.Publish(CompletionRequestReceived(requestCompletion))
            let l, c = getLineAndColumn requestCompletion.Code requestCompletion.CursorPosition
            let! declarationItems = script.GetCompletionItems(requestCompletion.Code, l, c)
            let completionItems =
                declarationItems
                |> Array.map completionItem
            context.Publish(CompletionRequestCompleted(completionItems, requestCompletion))
        }

    let handleCancelCurrentCommand (cancelCurrentCommand: CancelCurrentCommand) (context: KernelInvocationContext) =
        async {
            cancellationTokenSource.Cancel()
            cancellationTokenSource.Dispose()
            cancellationTokenSource <- new CancellationTokenSource()
            context.Publish(CurrentCommandCancelled(cancelCurrentCommand))
        }

    override __.HandleAsync(command: IKernelCommand, _context: KernelInvocationContext): Task =
        async {
            match command with
            | :? SubmitCode as submitCode -> submitCode.Handler <- fun invocationContext -> (handleSubmitCode submitCode invocationContext) |> Async.StartAsTask :> Task
            | :? RequestCompletion as requestCompletion -> requestCompletion.Handler <- fun invocationContext -> (handleRequestCompletion requestCompletion invocationContext) |> Async.StartAsTask :> Task
            | :? CancelCurrentCommand as cancelCurrentCommand -> cancelCurrentCommand.Handler <- fun invocationContext -> (handleCancelCurrentCommand cancelCurrentCommand invocationContext) |> Async.StartAsTask :> Task
            | _ -> ()
        } |> Async.StartAsTask :> Task
