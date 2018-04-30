﻿module Fake.Runtime.FakeRuntime

open System
open System.IO
open Fake.Runtime
open Paket

type FakeSection =
 | PaketDependencies of Paket.Dependencies * Lazy<Paket.DependenciesFile> * group : String option

let readAllLines (r : TextReader) =
  seq {
    let mutable line = r.ReadLine()
    while not (isNull line) do
      yield line
      line <- r.ReadLine()
  }
let private dependenciesFileName = "paket.dependencies"



#if !REMOVE_LEGACY_HEADER
type LegacyRawFakeSection =
  { Header : string
    Section : string }

let legacyReadFakeSection (scriptText:string) =
  let startString = "(* -- Fake Dependencies "
  let endString = "-- Fake Dependencies -- *)"
  let start = scriptText.IndexOf(startString) + startString.Length
  let endIndex = scriptText.IndexOf(endString) - 1
  if (start >= endIndex) then
    None
  else
    let fakeSectionWithVersion = scriptText.Substring(start, endIndex - start)
    let newLine = fakeSectionWithVersion.IndexOf("\n")
    let header = fakeSectionWithVersion.Substring(0, newLine).Trim()
    let fakeSection = fakeSectionWithVersion.Substring(newLine).Trim()
    Some { Header = header; Section = fakeSection}

let legacyParseHeader scriptCacheDir (f : LegacyRawFakeSection) =
  match f.Header with
  | "paket-inline" ->
    let dependenciesFile = Path.Combine(scriptCacheDir, dependenciesFileName)
    let fixedSection =
      f.Section.Split([| "\r\n"; "\r"; "\n" |], System.StringSplitOptions.None)
      |> Seq.map (fun line ->
        let replacePaketCommand (command:string) (line:string) =
          let trimmed = line.Trim()
          if trimmed.StartsWith command then
            let restString = trimmed.Substring(command.Length).Trim()
            let isValidPath = try Path.GetFullPath restString |> ignore; true with _ -> false
            let isAbsoluteUrl = match Uri.TryCreate(restString, UriKind.Absolute) with | true, _ -> true | _ -> false
            if isAbsoluteUrl || not isValidPath || Path.IsPathRooted restString then line
            else line.Replace(restString, Path.Combine("..", "..", restString))
          else line
        line
        |> replacePaketCommand "source"
        |> replacePaketCommand "cache"
      )
    File.WriteAllLines(dependenciesFile, fixedSection)
    PaketDependencies (Dependencies dependenciesFile, (lazy DependenciesFile.ReadFromFile dependenciesFile), None)
  | "paket.dependencies" ->
    let groupStart = "group "
    let fileStart = "file "
    let readLine (l:string) : (string * string) option =
      if l.StartsWith groupStart then ("group", (l.Substring groupStart.Length).Trim()) |> Some
      elif l.StartsWith fileStart then ("file", (l.Substring fileStart.Length).Trim()) |> Some
      elif String.IsNullOrWhiteSpace l then None
      else failwithf "Cannot recognise line in dependency section: '%s'" l
    let options =
      (use r = new StringReader(f.Section)
       readAllLines r |> Seq.toList)
      |> Seq.choose readLine
      |> dict
    let group =
      match options.TryGetValue "group" with
      | true, gr -> Some gr
      | _ -> None
    let file =
      match options.TryGetValue "file" with
      | true, depFile -> depFile
      | _ -> dependenciesFileName
    let fullpath = Path.GetFullPath file
    PaketDependencies (Dependencies fullpath, (lazy DependenciesFile.ReadFromFile fullpath), group)
  | _ -> failwithf "unknown dependencies header '%s'" f.Header

#endif

let tryReadPaketDependenciesFromScript defines cacheDir (scriptPath:string) (scriptText:string) =
  let pRefStr = "paket:"
  let grRefStr = "groupref"
  let groupReferences, paketLines =
    FSharpParser.findInterestingItems defines scriptPath scriptText
    |> Seq.choose (fun item -> 
        match item with
        | FSharpParser.InterestingItem.Reference ref when ref.StartsWith pRefStr ->
          let sub = ref.Substring (pRefStr.Length)
          Some (sub.TrimStart[|' '|])
        | _ -> None)
    |> Seq.toList
    |> List.partition (fun ref -> ref.StartsWith(grRefStr, System.StringComparison.OrdinalIgnoreCase))
  let paketCode =
    paketLines
    |> String.concat "\n"
  let paketGroupReferences =
    groupReferences
    |> List.map (fun groupRefString ->
      let raw = groupRefString.Substring(grRefStr.Length).Trim()
      let commentStart = raw.IndexOf "//"
      if commentStart >= 0 then raw.Substring(0, commentStart).Trim()
      else raw)

  if paketCode <> "" && paketGroupReferences.Length > 0 then
    failwith "paket code in combination with a groupref is currently not supported!"

  if paketGroupReferences.Length > 1 then
    failwith "multiple paket groupref are currently not supported!"

  if paketCode <> "" then
    let fixDefaults (paketCode:string) =
      let lines = paketCode.Split([|'\r';'\n'|]) |> Array.map (fun line -> line.ToLower().TrimStart())
      let storageRef = "storage"
      let sourceRef = "source"
      let frameworkRef = "framework"
      let restrictionRef = "restriction"
      let containsStorage = lines |> Seq.exists (fun line -> line.StartsWith(storageRef))
      let containsSource = lines |> Seq.exists (fun line -> line.StartsWith(sourceRef))
      let containsFramework = lines |> Seq.exists (fun line -> line.StartsWith(frameworkRef))
      let containsRestriction = lines |> Seq.exists (fun line -> line.StartsWith(restrictionRef))
      paketCode
      |> fun p -> if containsStorage then p else "storage: none" + "\n" + p
      |> fun p -> if containsSource then p else "source https://api.nuget.org/v3/index.json" + "\n" + p
      |> fun p -> if containsFramework || containsRestriction then p 
                  else "framework: netstandard2.0" + "\n" + p

    { Header = "paket-inline"
      Section = fixDefaults paketCode }
    |> legacyParseHeader cacheDir
    |> Some
  else
    let file = dependenciesFileName
    match paketGroupReferences with
    | [] ->
      None
    | group :: _ ->
      let fullpath = Path.GetFullPath file
      PaketDependencies (Dependencies fullpath, (lazy DependenciesFile.ReadFromFile fullpath), Some group)
      |> Some


type AssemblyData =
  { IsReferenceAssembly : bool
    Info : Runners.AssemblyInfo }

let paketCachingProvider (script:string) (logLevel:Trace.VerboseLevel) cacheDir (paketApi:Paket.Dependencies) (paketDependenciesFile:Lazy<Paket.DependenciesFile>) group =
  use __ = Fake.Profile.startCategory Fake.Profile.Category.Paket
  let groupStr = match group with Some g -> g | None -> "Main"
  let groupName = Paket.Domain.GroupName (groupStr)
#if DOTNETCORE
  //let framework = Paket.FrameworkIdentifier.DotNetCoreApp (Paket.DotNetCoreAppVersion.V2_0)
  let framework = Paket.FrameworkIdentifier.DotNetStandard (Paket.DotNetStandardVersion.V2_0)
#else
  let framework = Paket.FrameworkIdentifier.DotNetFramework (Paket.FrameworkVersion.V4_6)
#endif
  let lockFilePath = Paket.DependenciesFile.FindLockfile paketApi.DependenciesFile
  let parent s = Path.GetDirectoryName s
  let comb name s = Path.Combine(s, name)

#if DOTNETCORE
  let getCurrentSDKReferenceFiles() =
    // We need use "real" reference assemblies as using the currently running runtime assemlies doesn't work:
    // see https://github.com/fsharp/FAKE/pull/1695

    // Therefore we download the reference assemblies (the NETStandard.Library package)
    // and add them in addition to what we have resolved, 
    // we use the sources in the paket.dependencies to give the user a chance to overwrite.

    // Note: This package/version needs to updated together with our "framework" variable below and needs to 
    // be compatible with the runtime we are currently running on.
    let rootDir = Directory.GetCurrentDirectory()
    let packageName = Domain.PackageName("NETStandard.Library")
    let version = SemVer.Parse("2.0.2")
    let existingpkg = NuGetCache.GetTargetUserNupkg packageName version
    let extractedFolder =
      if File.Exists existingpkg then
        // Shortcut in order to prevent requests to nuget sources if we have it downloaded already
        Path.GetDirectoryName existingpkg
      else
        let sources = paketDependenciesFile.Value.Groups.[groupName].Sources
        let versions =
          Paket.NuGet.GetVersions false None rootDir (PackageResolver.GetPackageVersionsParameters.ofParams sources groupName packageName)
          |> Async.RunSynchronously
          |> dict
        let source =
          match versions.TryGetValue(version) with
          | true, v when v.Length > 0 -> v |> Seq.head
          | _ -> failwithf "Could not find package '%A' with version '%A' in any package source of group '%A', but fake needs this package to compile the script" packageName version groupName    
        
        let _, extractedFolder =
          Paket.NuGet.DownloadAndExtractPackage
            (None, rootDir, false, PackagesFolderGroupConfig.NoPackagesFolder,
             source, [], Paket.Constants.MainDependencyGroup,
             packageName, version, PackageResolver.ResolvedPackageKind.Package, false, false, false, false)
          |> Async.RunSynchronously
        extractedFolder        
    //let netstandard = System.Runtime.Loader.AssemblyLoadContext.Default.LoadFromAssemblyName(System.Reflection.AssemblyName("netstandard"))
    let sdkDir = Path.Combine(extractedFolder, "build", "netstandard2.0", "ref")
    Directory.GetFiles(sdkDir, "*.dll")
    |> Seq.toList
#endif

  let writeIntellisenseFile cacheDir (context : Paket.LoadingScripts.ScriptGeneration.PaketContext) =
    // Write loadDependencies file (basically only for editor support)
    let intellisenseFile = Path.Combine (cacheDir, "intellisense.fsx")
    if logLevel.PrintVerbose then Trace.log <| sprintf "Writing '%s'" intellisenseFile
    let groupScripts = Paket.LoadingScripts.ScriptGeneration.generateScriptContent context
    let _, groupScript =
      match groupScripts with
      | [] -> failwith "generateScriptContent returned []"
      | [h] -> failwithf "generateScriptContent returned a single item: %A" h
      | [ _, scripts; _, [groupScript] ] -> scripts, groupScript
      | _ -> failwithf "generateScriptContent returned %A" groupScripts

    
    let rootDir = DirectoryInfo cacheDir
    //for sd in scripts do
    //    let scriptPath = Path.Combine (rootDir.FullName , sd.PartialPath)
    //    let scriptDir = Path.GetDirectoryName scriptPath |> Path.GetFullPath |> DirectoryInfo
    //    scriptDir.Create()
    //    sd.Save rootDir

    let content = groupScript.RenderDirect rootDir (FileInfo intellisenseFile)

    // TODO: Make sure to create #if !FAKE block, because we don't actually need it.
    let intellisenseContents =
      [| "// This file is automatically generated by FAKE"
         "// This file is needed for IDE support only"
         "#if !FAKE"
         content
         "#endif" |]
    File.WriteAllLines (intellisenseFile, intellisenseContents)

  let restoreOrUpdate () =
    if logLevel.PrintVerbose then Trace.log "Restoring with paket..."

    // Update
    let localLock = script + ".lock" // the primary lockfile-path </> lockFilePath.FullName is implementation detail
    let needLocalLock = lockFilePath.FullName.Contains (Path.GetFullPath cacheDir) // Only primary if not external already.
    let localLockText = lazy File.ReadAllText localLock
    if needLocalLock && File.Exists localLock && (not (File.Exists lockFilePath.FullName) || localLockText.Value <> File.ReadAllText lockFilePath.FullName) then
      File.Copy(localLock, lockFilePath.FullName)
    if needLocalLock && not (File.Exists localLock) then
      File.Delete lockFilePath.FullName
    if not <| File.Exists lockFilePath.FullName then
      if logLevel.PrintVerbose then Trace.log "Lockfile was not found. We will update the dependencies and write our own..."
      try
        paketApi.UpdateGroup(groupStr, false, false, false, false, false, Paket.SemVerUpdateMode.NoRestriction, false)
        |> ignore
      with
      | e when e.Message.Contains "Did you restore groups" ->
        // See https://github.com/fsharp/FAKE/issues/1672
        // and https://github.com/fsprojects/Paket/issues/2785
        // We do a restore anyway.
        ()
      if needLocalLock then File.Copy(lockFilePath.FullName, localLock)
    
    // TODO: Check if restore is up-to date and skip all paket calls (load assembly-list from a new cache)
    
    // Restore
    paketApi.Restore((*false, group, [], false, true*))
    |> ignore

    let lockFile = LockFile.LoadFrom(lockFilePath.FullName)
    match lockFile.Groups |> Map.tryFind groupName with
    | Some g -> ()
    | None -> failwithf "The group '%s' was not found in the lockfile. You might need to run 'paket install' first!" groupName.Name
    
    let (cache:DependencyCache) = DependencyCache(lockFile)
    let orderedGroup = cache.OrderedGroups groupName // lockFile.GetGroup groupName
    
    //dependencyCacheProfile.Dispose()

    let rid =
#if DOTNETCORE
        let ridString = Microsoft.DotNet.PlatformAbstractions.RuntimeEnvironment.GetRuntimeIdentifier()
#else
        let ridString = "win"
#endif
        Paket.Rid.Of(ridString)

    // get runtime graph
    let graph =
      async {
        if logLevel.PrintVerbose then Trace.log <| sprintf "Calculating the runtime graph..."
        use runtimeGraphProfile = Fake.Profile.startCategory Fake.Profile.Category.PaketRuntimeGraph
        let result =
          orderedGroup
          |> Seq.choose (fun p ->
            RuntimeGraph.getRuntimeGraphFromNugetCache cacheDir (Some PackagesFolderGroupConfig.NoPackagesFolder) groupName p.Resolved)
          |> RuntimeGraph.mergeSeq
        runtimeGraphProfile.Dispose()
        return result
      }
      |> Async.StartAsTask

    // Restore load-script, as we don't need it create it in the background.
    let writeIntellisenseTask = 
      async {
        try
            writeIntellisenseFile cacheDir {
              Cache = cache
              ScriptType = Paket.LoadingScripts.ScriptGeneration.ScriptType.FSharp
              Groups = [groupName]
              DefaultFramework = false, (Paket.FrameworkIdentifier.DotNetFramework (Paket.FrameworkVersion.V4_7_1))
            }
        with e ->
            eprintfn "Failed to write intellisense script: %O" e
      } |> Async.StartAsTask

    let filterValidAssembly (isSdk, isReferenceAssembly, fi:FileInfo) =
        let fullName = fi.FullName
        try let assembly = Mono.Cecil.AssemblyDefinition.ReadAssembly fullName
            { IsReferenceAssembly = isReferenceAssembly
              Info =
                { Runners.AssemblyInfo.FullName = assembly.Name.FullName
                  Runners.AssemblyInfo.Version = assembly.Name.Version.ToString()
                  Runners.AssemblyInfo.Location = fullName } } |> Some
        with e -> 
            if logLevel.PrintVerbose then Trace.log <| sprintf "Could not load '%s': %O" fullName e
            None

    // Retrieve assemblies
    use __ = Fake.Profile.startCategory Fake.Profile.Category.PaketGetAssemblies
    if logLevel.PrintVerbose then Trace.log <| sprintf "Retrieving the assemblies (rid: '%O')..." rid
    orderedGroup
    |> Seq.filter (fun p ->
      if p.Name.ToString() = "Microsoft.FSharp.Core.netcore" then
        eprintfn "Ignoring 'Microsoft.FSharp.Core.netcore' please tell the package authors to fix their package and reference 'FSharp.Core' instead."
        false
      else true)
    |> Seq.map (fun p -> async {
      match cache.InstallModel groupName p.Name with
      | None -> return failwith "InstallModel not cached?"
      | Some installModelRaw ->
      let installModel =
        installModelRaw
          .ApplyFrameworkRestrictions(Paket.Requirements.getExplicitRestriction p.Settings.FrameworkRestrictions)
      let targetProfile = Paket.TargetProfile.SinglePlatform framework

      let refAssemblies =
        installModel.GetCompileReferences targetProfile
        |> Seq.map (fun fi -> true, FileInfo fi.Path)
        //|> Seq.toList
      let runtimeAssemblies =
        installModel.GetRuntimeAssemblies graph.Result rid targetProfile
        |> Seq.map (fun fi -> false, FileInfo fi.Library.Path)
        //|> Seq.toList
      let result =
        Seq.append runtimeAssemblies refAssemblies
        |> Seq.filter (fun (_, r) -> r.Extension = ".dll" || r.Extension = ".exe" )
        |> Seq.map (fun (isRef, fi) -> false, isRef, fi)
        |> Seq.choose filterValidAssembly
        |> Seq.toList
      return result })
    |> Seq.toArray
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Seq.collect id
#if DOTNETCORE
    // Append sdk files as references in order to properly compile, for runtime we can default to the default-load-context.
    |> Seq.append
        (getCurrentSDKReferenceFiles()
         |> Seq.map (fun file -> true, true, FileInfo file)
         |> Seq.choose filterValidAssembly)
#endif  
    //|> Async.Parallel
    //|> Async.RunSynchronously
    //|> Seq.choose id
    // If we have multiple select one
    |> Seq.groupBy (fun ass -> ass.IsReferenceAssembly, System.Reflection.AssemblyName(ass.Info.FullName).Name)
    |> Seq.map (fun (_, group) -> group |> Seq.maxBy(fun ass -> ass.Info.Version))
    |> Seq.toList

  // Restore or update immediatly, because or everything might be OK -> cached path.
  let knownAssemblies = restoreOrUpdate()

  if logLevel.PrintVerbose then
    Trace.tracefn "Known assemblies: \n\t%s" (System.String.Join("\n\t", knownAssemblies |> Seq.map (fun a -> sprintf " - %s: %s (%s)" (if a.IsReferenceAssembly then "ref" else "lib") a.Info.Location a.Info.Version)))
  { new CoreCache.ICachingProvider with
      member x.CleanCache context =
        if logLevel.PrintVerbose then Trace.log "Invalidating cache..."
        let assemblyPath, warningsFile = context.CachedAssemblyFilePath + ".dll", context.CachedAssemblyFilePath + ".warnings"
        try File.Delete warningsFile; File.Delete assemblyPath
        with e -> Trace.traceError (sprintf "Failed to delete cached files: %O" e)
      member __.TryLoadCache (context) =
          let references =
              knownAssemblies
              |> List.filter (fun a -> a.IsReferenceAssembly)
              |> List.map (fun (a:AssemblyData) -> a.Info.Location)
          let runtimeAssemblies =
              knownAssemblies
              |> List.filter (fun a -> not a.IsReferenceAssembly)
              |> List.map (fun a -> a.Info)
          let fsiOpts = context.Config.CompileOptions.AdditionalArguments |> Yaaf.FSharp.Scripting.FsiOptions.ofArgs
          let newAdditionalArgs =
              { fsiOpts with
                  NoFramework = true
                  Debug = Some Yaaf.FSharp.Scripting.DebugMode.Portable }
              |> (fun options -> options.AsArgs)
              |> Seq.toList
          { context with
              Config =
                { context.Config with
                    CompileOptions =
                      { context.Config.CompileOptions with
                          AdditionalArguments = newAdditionalArgs
                          RuntimeDependencies = runtimeAssemblies @ context.Config.CompileOptions.RuntimeDependencies
                          CompileReferences = references @ context.Config.CompileOptions.CompileReferences
                      }
                }
          },
          let assemblyPath, warningsFile = context.CachedAssemblyFilePath + ".dll", context.CachedAssemblyFilePath + ".warnings"
          if File.Exists (assemblyPath) && File.Exists (warningsFile) then
              Some { CompiledAssembly = assemblyPath; Warnings = File.ReadAllText(warningsFile) }
          else None
      member x.SaveCache (context, cache) =
          if logLevel.PrintVerbose then Trace.log "saving cache..."
          File.WriteAllText (context.CachedAssemblyFilePath + ".warnings", cache.Warnings) }

let restoreDependencies script logLevel cacheDir section =
  match section with
  | PaketDependencies (paketDependencies, paketDependenciesFile, group) ->
    paketCachingProvider script logLevel cacheDir paketDependencies paketDependenciesFile group

let tryFindGroupFromDepsFile scriptDir =
    let depsFile = Path.Combine(scriptDir, "paket.dependencies")
    if File.Exists (depsFile) then
        match
            File.ReadAllLines(depsFile)
            |> Seq.map (fun l -> l.Trim())
            |> Seq.fold (fun (takeNext, result) l ->
                // find '// [ FAKE GROUP ]' and take the next one.
                match takeNext, result with
                | _, Some s -> takeNext, Some s
                | true, None ->
                    if not (l.ToLowerInvariant().StartsWith "group") then
                        Trace.traceFAKE "Expected a group after '// [ FAKE GROUP]' comment, but got %s" l
                        false, None
                    else
                        let splits = l.Split([|" "|], StringSplitOptions.RemoveEmptyEntries)
                        if splits.Length < 2 then
                            Trace.traceFAKE "Expected a group name after '// [ FAKE GROUP]' comment, but got %s" l
                            false, None
                        else
                            false, Some (splits.[1])
                | _ -> if l.Contains "// [ FAKE GROUP ]" then true, None else false, None) (false, None)
            |> snd with
        | Some group ->
            let fullpath = Path.GetFullPath depsFile
            PaketDependencies (Dependencies fullpath, (lazy DependenciesFile.ReadFromFile fullpath), Some group)
            |> Some
        | _ -> None
    else None

let prepareFakeScript defines logLevel script =
    // read dependencies from the top
    let scriptDir = Path.GetDirectoryName (script)
    let cacheDir = Path.Combine(scriptDir, ".fake", Path.GetFileName(script))
    Directory.CreateDirectory (cacheDir) |> ignore
    let section =
        use __ = Fake.Profile.startCategory Fake.Profile.Category.Analyzing
        let scriptText = File.ReadAllText(script)
        let newSection = tryReadPaketDependenciesFromScript defines cacheDir script scriptText
        match legacyReadFakeSection scriptText with
        | Some s ->
          Trace.traceFAKE "Legacy header is no longer supported and will be removed soon, please upgrade to '#r \"paket: nuget FakeModule //\" in paket syntax (consult the docs)."
          match newSection with
          | Some s -> Some s // prefer new method (but print warning)
          | None -> legacyParseHeader cacheDir s |> Some
        | None ->
            match newSection with
            | Some s -> Some s
            | None ->
              tryFindGroupFromDepsFile scriptDir

    match section with
    | Some section ->
        restoreDependencies script logLevel cacheDir section
    | None ->
        let defaultPaketCode = """
source https://api.nuget.org/v3/index.json
storage: none
framework: netstandard2.0
nuget FSharp.Core
        """
        if Environment.environVar "FAKE_ALLOW_NO_DEPENDENCIES" <> "true" then
          Trace.traceFAKE """Consider adding your dependencies via `#r` dependencies, for example add '#r "paket: nuget FSharp.Core //"'.
See https://fake.build/fake-fake5-modules.html for details. 
If you know what you are doing you can silence this warning by setting the environment variable 'FAKE_ALLOW_NO_DEPENDENCIES' to 'true'"""
        let section =
          { Header = "paket-inline"
            Section = defaultPaketCode }
          |> legacyParseHeader cacheDir        
        restoreDependencies script logLevel cacheDir section

let prepareAndRunScriptRedirect (logLevel:Trace.VerboseLevel) (fsiOptions:string list) scriptPath scriptArgs onErrMsg onOutMsg useCache =

  if logLevel.PrintVerbose then Trace.log (sprintf "prepareAndRunScriptRedirect(Script: %s, fsiOptions: %A)" scriptPath (System.String.Join(" ", fsiOptions)))
  let fsiOptionsObj = Yaaf.FSharp.Scripting.FsiOptions.ofArgs fsiOptions
  // TODO: this is duplicated in CoreCache :(
  let newFsiOptions =
    { fsiOptionsObj with
#if !NETSTANDARD1_6
        Defines = "FAKE" :: fsiOptionsObj.Defines
#else
        Defines = "DOTNETCORE" :: "FAKE" :: fsiOptionsObj.Defines
#endif
      }
  let provider = prepareFakeScript newFsiOptions.Defines logLevel scriptPath
  use out = Yaaf.FSharp.Scripting.ScriptHost.CreateForwardWriter onOutMsg
  use err = Yaaf.FSharp.Scripting.ScriptHost.CreateForwardWriter onErrMsg
  let config =
    { Runners.FakeConfig.VerboseLevel = logLevel
      Runners.FakeConfig.ScriptFilePath = scriptPath
      Runners.FakeConfig.CompileOptions =
        { CompileReferences = []
          RuntimeDependencies = []
          AdditionalArguments = fsiOptions }
      Runners.FakeConfig.UseCache = useCache
      Runners.FakeConfig.Out = out
      Runners.FakeConfig.Err = err
      Runners.FakeConfig.ScriptArgs = scriptArgs }
  CoreCache.runScriptWithCacheProvider config provider

let inline prepareAndRunScript logLevel fsiOptions scriptPath scriptArgs useCache =
  prepareAndRunScriptRedirect logLevel fsiOptions scriptPath scriptArgs (printf "%s") (printf "%s") useCache

