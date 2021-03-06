// --------------------------------------------------------------------------------------
// Main file - contains types that call F# compiler service in the background, display
// error messages and expose various methods for to be used from MonoDevelop integration
// --------------------------------------------------------------------------------------

namespace MonoDevelop.FSharp
#nowarn "40"

open System
open System.IO
open System.Xml
open System.Text
open System.Diagnostics
open Mono.TextEditor
open MonoDevelop.Ide
open MonoDevelop.Core
open MonoDevelop.Projects
open Microsoft.FSharp.Compiler.SourceCodeServices
open ExtCore
open ExtCore.Caching
open ExtCore.Control

module Symbols =

  ///Given a column and line string returns the identifier portion of the string
  let lastIdent column lineString =
      match FSharp.CompilerBinding.Parsing.findLongIdents(column, lineString) with
      | Some (_, identIsland) -> Seq.last identIsland
      | None -> ""

  ///Returns a TextSegment that is trimmed to only include the identifier
  let getTextSegment (doc:TextDocument) (symbolUse:FSharpSymbolUse) column line =
    let lastIdent = lastIdent  column line
    let (startLine, startColumn), (endLine, endColumn) = FSharp.CompilerBinding.Symbols.trimSymbolRegion symbolUse lastIdent

    let startOffset = doc.LocationToOffset(startLine, startColumn+1)
    let endOffset = doc.LocationToOffset(endLine, endColumn+1)
    TextSegment.FromBounds(startOffset, endOffset)

module Option =
    let tryCast<'a> (o: obj): 'a option = 
        match o with
        | null -> None
        | :? 'a as a -> Some a
        | _ -> Printf.kprintf System.Diagnostics.Debug.Fail "Cannot cast %O to %O" (o.GetType()) typeof<'a>.Name
               None

/// Formatting of tool-tip information displayed in F# IntelliSense
module internal TipFormatter =

  ///lru based memoize
  let memoize f n =
      let lru = ref (LruCache.create n)
      fun x -> match (!lru).TryFind x with
               | Some entry, cache ->
                   lru := cache
                   entry
               | None, cache ->
                   let res = f x
                   lru := cache.Add (x, res)
                   LoggingService.LogInfo <| sprintf "cache contains %i entries\n%A" (!lru).Count ( (!lru).ToArray () |> Array.map (fst >> Path.GetFileName))
                   res

  /// Memoize the objects that manage access to XML files, keeping only 20 most used
  // @todo consider if this needs to be a weak table in some way
  let xmlDocProvider =
      memoize (fun x ->
          try Some (ICSharpCode.NRefactory.Documentation.XmlDocumentationProvider(x))
          with exn -> None) 20u

  let tryExt file ext = Option.condition File.Exists (Path.ChangeExtension(file,ext))

  /// Return the XmlDocumentationProvider for an assembly
  let findXmlDocProviderForAssembly file  =
      maybe {let! xmlFile = Option.coalesce (tryExt file "xml") (tryExt file "XML")
             return! xmlDocProvider xmlFile }
            
  let findXmlDocProviderForEntity (file, key:string)  =
      maybe {let! docReader = findXmlDocProviderForAssembly file
             let doc = docReader.GetDocumentation key
             if String.IsNullOrEmpty doc then return! None
             else return doc}

  let (|MemberName|_|) (name:string) =
      let dotRight = name.LastIndexOf '.'
      if dotRight < 1 || dotRight >= name.Length - 1 then None else
      let typeName = name.[0..dotRight-1]
      let elemName = name.[dotRight+1..]
      Some ("T:" + typeName, elemName)

  let (|Method|_|) (key:string) =
     if key.StartsWith "M:" then
         let key = key.[2..]
         let name,count,args =
             if not (key.Contains "(") then key, 0, [| |] else

             let pieces = key.Split( [|'('; ')' |], StringSplitOptions.RemoveEmptyEntries)
             if pieces.Length < 2 then key, 0, [| |] else
             let nameAndCount = pieces.[0]
             let argsText = pieces.[1].Replace(")","")
             let args = argsText.Split(',')
             if nameAndCount.Contains "`" then
                 let ps = nameAndCount.Split( [| '`' |],StringSplitOptions.RemoveEmptyEntries)
                 let noArgs =
                     try int (ps.[1].Split([| '.' |], StringSplitOptions.RemoveEmptyEntries).[0] )
                     with _ -> 0

                 nameAndCount, noArgs, args
             else
                 nameAndCount, 0, args

         match name with
         | MemberName(typeName,elemName) -> Some (typeName, elemName, count, args)
         | _ -> None
     else None

  let (|FieldPropertyOrEvent|_|) (key:string) =
     if key.StartsWith "P:" || key.StartsWith "F:" || key.StartsWith "E:" then
         let name = key.[2..]
         match name with
         | MemberName(typeName,elemName) -> Some (typeName, elemName)
         | _ -> None
     else None

  let (|Type|_|) (key:string) =
     if key.StartsWith "T:" then
        Some key
     else None

  let trySelectOverload (nodes: XmlNodeList, argsFromKey:string[]) =

      if (nodes.Count = 1) then Some nodes.[0] else

      let result =
        [ for x in nodes -> x ] |> Seq.tryFind (fun curNode ->
          let paramList = curNode.SelectNodes ("Parameters/*")
          let paramTypes = [| for p in paramList -> p.Attributes.GetNamedItem("Type").Value |]
          (paramList <> null) && (argsFromKey.Length = paramList.Count) && paramTypes = argsFromKey )

      match result with
      | None -> None
      | Some node ->
          let docs = node.SelectSingleNode ("Docs")
          if docs = null then None else Some docs

  ///check helpxml exist
  let tryGetDoc key =
    let helpTree = MonoDevelop.Projects.HelpService.HelpTree
    if helpTree = null then None else
    try
        let helpxml = helpTree.GetHelpXml(key)
        if helpxml = null then None else Some(helpxml)
    with ex ->
        LoggingService.LogError (sprintf "GetHelpXml failed for key %s" key, ex)
        None

  let typeMemberFormatter name =
      if name = "#ctor" then "/Type/Members/Member[@MemberName='.ctor']"
      else "/Type/Members/Member[@MemberName='" + name + "']"

  /// Try to find the MonoDoc documentation for a file/key pair representing an entity with documentation
  let findMonoDocProviderForEntity (_file, key) =

      match key with
      | Type(typ) ->
          maybe {let! docXml = tryGetDoc typ
                 return docXml.OuterXml}
      | FieldPropertyOrEvent (parentId, name) ->
          maybe {let! doc = tryGetDoc (parentId)
                 let docXml = doc.SelectSingleNode (typeMemberFormatter name)
                 return docXml.OuterXml }
      | Method(parentId, name, _count, args) ->
          maybe {
                  let! doc = tryGetDoc (parentId)
                  let nodeXmls = doc.SelectNodes (typeMemberFormatter name)
                  let! docXml = trySelectOverload (nodeXmls, args)
                  return docXml.OuterXml }
      | _ -> LoggingService.LogWarning <| sprintf "findMonoDocProviderForEntity, No match for key = %s" key
             None

  /// Find the documentation for a file/key pair representing an entity with documentation
  let findDocForEntity (file, key)  =
      match findXmlDocProviderForEntity (file, key) with
      | Some doc -> Some doc
      | None -> findMonoDocProviderForEntity (file, key)

  /// Format some of the data returned by the F# compiler
  let private buildFormatComment cmt =
    match cmt with
    | FSharpXmlDoc.Text(s) -> Tooltips.getTooltip Styles.simpleMarkup <| s.Trim()
    | FSharpXmlDoc.XmlDocFileSignature(file,key) ->
        match findDocForEntity (file, key) with
        | None -> String.Empty
        | Some doc -> Tooltips.getTooltip Styles.simpleMarkup doc
    | _ -> String.Empty

  /// Format some of the data returned by the F# compiler
  let private buildFormatElement el =
    let signatureB, commentB = StringBuilder(), StringBuilder()
    match el with
    | FSharpToolTipElement.None -> ()
    | FSharpToolTipElement.Single(it, comment) ->
        Debug.WriteLine("DataTipElement: " + it)
        signatureB.Append(GLib.Markup.EscapeText (it)) |> ignore
        let html = buildFormatComment comment
        if not (String.IsNullOrWhiteSpace html) then
            commentB.Append(html) |> ignore
    | FSharpToolTipElement.Group(items) ->
        let items, msg =
          if items.Length > 10 then
            (items |> Seq.take 10 |> List.ofSeq), sprintf "   <i>(+%d other overloads)</i>" (items.Length - 10)
          else items, null
        if (items.Length > 1) then
          signatureB.AppendLine("Multiple overloads") |> ignore
        items |> Seq.iteri (fun i (it,comment) ->
          signatureB.Append(GLib.Markup.EscapeText (it))  |> ignore
          if i = 0 then
              let html = buildFormatComment comment
              if not (String.IsNullOrWhiteSpace html) then
                  commentB.AppendLine(html) |> ignore
                  commentB.Append(GLib.Markup.EscapeText "\n")  |> ignore )
        if msg <> null then signatureB.Append(msg) |> ignore
    | FSharpToolTipElement.CompositionError(err) ->
        signatureB.Append("Composition error: " + GLib.Markup.EscapeText(err)) |> ignore
    signatureB.ToString().Trim(), commentB.ToString().Trim()

  /// Format tool-tip that we get from the language service as string
  //
  // TODO: Use the current projects policy to get line length
  // Document.Project.Policies.Get<TextStylePolicy>(types) or fall back to:
  // MonoDevelop.Projects.Policies.PolicyService.GetDefaultPolicy<TextStylePolicy (types)
  let formatTip (FSharpToolTipText(list)) =
      [ for item in list ->
          let signature, summary = buildFormatElement item
          signature, summary ]

  /// For elements with XML docs, the parameter descriptions are buried in the XML. Fetch it.
  let private extractParamTipFromComment paramName comment =
    match comment with
    | FSharpXmlDoc.Text(s) -> Tooltips.getParameterTip Styles.simpleMarkup s paramName
    // For 'FSharpXmlDoc.XmlDocFileSignature' we can get documentation from 'xml' files, and via MonoDoc on Mono
    | FSharpXmlDoc.XmlDocFileSignature(file,key) ->
        maybe {let! docReader = findXmlDocProviderForAssembly file
               let doc = docReader.GetDocumentation(key)
               if String.IsNullOrEmpty doc then return! None else
               let parameterTip = Tooltips.getParameterTip Styles.simpleMarkup doc paramName
               return! parameterTip}
    | _ -> None

  /// For elements with XML docs, the parameter descriptions are buried in the XML. Fetch it.
  let private extractParamTipFromElement paramName element =
      match element with
      | FSharpToolTipElement.None -> None
      | FSharpToolTipElement.Single (_it, comment) -> extractParamTipFromComment paramName comment
      | FSharpToolTipElement.Group items -> List.tryPick (snd >> extractParamTipFromComment paramName) items
      | FSharpToolTipElement.CompositionError _err -> None

  /// For elements with XML docs, the parameter descriptions are buried in the XML. Fetch it.
  let extractParamTip paramName (FSharpToolTipText elements) =
      List.tryPick (extractParamTipFromElement paramName) elements


module internal MonoDevelop =
    let getLineInfoFromOffset (offset, doc:Mono.TextEditor.TextDocument) =
        let loc  = doc.OffsetToLocation(offset)
        let line, col = max loc.Line 1, loc.Column-1
        let currentLine = doc.GetLineByOffset(offset)
        let lineStr = doc.Text.Substring(currentLine.Offset, currentLine.EndOffset - currentLine.Offset)
        (line, col, lineStr)

    ///gets the projectFilename, sourceFiles, commandargs from the project and current config
    let getCheckerArgsFromProject(project:DotNetProject, config) =
        let files = CompilerArguments.getSourceFiles(project.Items) |> Array.ofList
        let fileName = project.FileName.ToString()
        let arguments =
            maybe {let! projConfig = project.GetConfiguration(config) |> Option.tryCast<DotNetProjectConfiguration>
                   let! fsconfig = projConfig.CompilationParameters |> Option.tryCast<FSharpCompilerParameters>
                   let args = CompilerArguments.generateCompilerOptions(project,
                                                                        fsconfig,
                                                                        None,
                                                                        CompilerArguments.getTargetFramework projConfig.TargetFramework.Id,
                                                                        config,
                                                                        false) |> Array.ofList
                   return args }

        match arguments with
        | Some args -> fileName, files, args
        | None -> LoggingService.LogWarning ("F# project checker options could not be retrieved, falling back to default options")
                  fileName, files, [||]

    let getConfig () =
        match MonoDevelop.Ide.IdeApp.Workspace with
        | ws when ws <> null && ws.ActiveConfiguration <> null -> ws.ActiveConfiguration
        | _ -> MonoDevelop.Projects.ConfigurationSelector.Default

    let getCheckerArgs(project: Project, filename: string) =
        match project with
        | :? DotNetProject as dnp when FSharp.CompilerBinding.LanguageService.IsAScript filename ->
            getCheckerArgsFromProject(dnp, getConfig())
        | _ -> filename, [|filename|], [||]

/// Provides functionality for working with the F# interactive checker running in background
open Microsoft.FSharp.Compiler.AbstractIL.Internal.Library
type MDLanguageService() =
  /// Single instance of the language service. We don't want the VFS during tests, so set it to blank from tests
  /// before Instance is evaluated
  static let mutable vfs =
      lazy (let originalFs = Shim.FileSystem
            let fs = new FileSystem(originalFs, (fun () -> seq { yield! IdeApp.Workbench.Documents }))
            Shim.FileSystem <- fs
            fs :> IFileSystem)

  static let mutable instance =
    lazy
        let _ = vfs.Force()
        new FSharp.CompilerBinding.LanguageService(
            (fun changedfile ->
                DispatchService.GuiDispatch(fun () ->
                    try let doc = IdeApp.Workbench.ActiveDocument
                        if doc <> null && doc.FileName.FullPath.ToString() = changedfile then
                            LoggingService.LogInfo(sprintf "FSharp Language Service: Compiler requesting reparse of document '%s'." (Path.GetFileName changedfile))
                            doc.ReparseDocument()
                    with exn  -> () )))

  static member Instance with get () = instance.Force ()
                         and  set v  = instance <- lazy v
  // Call this before Instance is called
  static member DisableVirtualFileSystem() =
        vfs <- lazy (Shim.FileSystem)

          /// Is the specified extension supported F# file?
  static member SupportedFileName fileName =
    let ext = Path.GetExtension fileName
    [".fsscript"; ".fs"; ".fsx"; ".fsi"; ".sketchfs"] |> List.exists ((=) ext)

/// Various utilities for working with F# language service
module internal ServiceUtils =
  let map =
    [ 0x0000, "md-class"; 0x0003, "md-enum"; 0x00012, "md-struct";
      0x00018, "md-struct" (* value type *); 0x0002, "md-delegate"; 0x0008, "md-interface";
      0x000e, "md-class" (* module *); 0x000f, "md-name-space"; 0x000c, "md-method";
      0x000d, "md-extensionmethod" (* method2 ? *); 0x00011, "md-property";
      0x0005, "md-event"; 0x0007, "md-field" (* fieldblue ? *);
      0x0020, "md-field" (* fieldyellow ? *); 0x0001, "md-field" (* const *);
      0x0004, "md-property" (* enummember *); 0x0006, "md-class" (* exception *);
      0x0009, "md-text-file-icon" (* TextLine *); 0x000a, "md-regular-file" (* Script *);
      0x000b, "Script" (* Script2 *); 0x0010, "md-tip-of-the-day" (* Formula *);
      0x00013, "md-class" (* Template *); 0x00014, "md-class" (* Typedef *);
      0x00015, "md-class" (* Type *); 0x00016, "md-struct" (* Union *);
      0x00017, "md-field" (* Variable *); 0x00019, "md-class" (* Intrinsic *);
      0x0001f, "md-breakpint" (* error *); 0x00021, "md-misc-files" (* Misc1 *);
      0x0022, "md-misc-files" (* Misc2 *); 0x00023, "md-misc-files" (* Misc3 *); ] |> Map.ofSeq

  /// Translates icon code that we get from F# language service into a MonoDevelop icon
  let getIcon glyph =
    match map.TryFind (glyph / 6), map.TryFind (glyph % 6) with
    | Some(s), _ -> s // Is the second number good for anything?
    | _, _ -> "md-breakpoint"
