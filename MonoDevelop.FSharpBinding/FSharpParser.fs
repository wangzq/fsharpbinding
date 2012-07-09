﻿namespace MonoDevelop.FSharp

open System
open MonoDevelop.Ide
open MonoDevelop.Ide.Gui
open MonoDevelop.Core
open MonoDevelop.Ide.TypeSystem
open ICSharpCode.NRefactory.TypeSystem

type FSharpParsedDocument(fileName) = 
     inherit DefaultParsedDocument(fileName)
  

type FSharpParser() =
  inherit AbstractTypeSystemParser()
  do Debug.tracef "Parsing" "Creating FSharpParser"
  
  //override x.CanParse(fileName) =
  //  Common.supportedExtension(IO.Path.GetExtension(fileName))
      
  let prevErrors = System.Collections.Generic.Dictionary<string,Error list>()
  let prevContent = System.Collections.Generic.Dictionary<string,string>()
  interface ITypeSystemParser with
   override x.Parse(storeAst:bool, fileName:string, content:System.IO.TextReader, proj:MonoDevelop.Projects.Project) =
    let fileContent = content.ReadToEnd()
    Debug.tracef "Parsing" "Update in FSharpParser.Parse"
  
    // Trigger a parse/typecheck in the background. After the parse/typecheck is completed, request another parse to report the errors.
    //
    // Skip this is this call is a result of updating errors and the content still matches.
    if not (prevContent.ContainsKey(fileName) && prevContent.[fileName] = fileContent ) && Common.supportedExtension(IO.Path.GetExtension(fileName)) then 
      // Trigger parsing in the language service 
      let config = IdeApp.Workspace.ActiveConfiguration
      let filePathOpt = 
          // TriggerParse will work only for full paths
          if IO.Path.IsPathRooted(fileName) then 
              Some(FilePath(fileName) )
          elif IdeApp.Workbench.ActiveDocument <> null then
             let file = IdeApp.Workbench.ActiveDocument.FileName
             if file.FullPath.ToString() <> "" then Some file else None
          else None
      match filePathOpt with 
      | None -> ()
      | Some filePath -> 
          LanguageService.Service.TriggerParse(filePath, fileContent, proj, config, full=false, afterCompleteTypeCheckCallback=(fun (fileName,errors) ->

                      let file = fileName.FullPath.ToString()
                      prevErrors.[file] <- errors
                      prevContent.[file] <- fileContent
                      // Scheule another parse to actually update the errors 
                      try 
                         let doc = IdeApp.Workbench.ActiveDocument
                         if doc.FileName.FullPath.ToString() = file then 
                             Debug.tracef "Parsing" "Requesting re-parse of file '%s' because some errors were reported asynchronously and we should return a new document showing these" file
                             doc.ReparseDocument()
                      with _ -> ()))

    // Create parsed document with the results from the last type-checking      
    // (we could wait, but that would probably take a long time)
    let doc = new FSharpParsedDocument(fileName)
    doc.Flags <- doc.Flags ||| ParsedDocumentFlags.NonSerializable
    let errors = 
        match prevErrors.TryGetValue(fileName) with 
        | true,err -> 
            prevContent.Remove(fileName) |> ignore; 
            err
        | _ -> [ ] 

    for er in errors do 
        Debug.tracef "Parsing" "Adding error, message '%s', region '%A'" er.Message (er.Region.BeginLine,er.Region.BeginColumn,er.Region.EndLine,er.Region.EndColumn)
        doc.Errors.Add(er)    

    doc.LastWriteTimeUtc <- (try System.IO.File.GetLastWriteTimeUtc (fileName) with _ -> DateTime.UtcNow) 
    doc :> ParsedDocument