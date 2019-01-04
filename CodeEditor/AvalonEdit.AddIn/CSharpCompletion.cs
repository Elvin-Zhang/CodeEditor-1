﻿using AvalonEdit.AddIn.DataItems;
using AvalonEdit.AddIn.Util;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Completion;
using ICSharpCode.NRefactory.Documentation;
using ICSharpCode.NRefactory.Editor;
using ICSharpCode.NRefactory.TypeSystem;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;

namespace AvalonEdit.AddIn
{
    public class CSharpCompletion
    {
        private IProjectContent projectContent;

        public CSharpCompletion(IReadOnlyList<Assembly> assemblies = null)
        {
            projectContent = new CSharpProjectContent();
            if (assemblies == null)
            {
                assemblies = new List<Assembly>
                {
                    typeof(object).Assembly, // mscorlib
                    typeof(Uri).Assembly, // System.dll
                    typeof(Enumerable).Assembly, // System.Core.dll
                    //					typeof(System.Xml.XmlDocument).Assembly, // System.Xml.dll
                    //					typeof(System.Drawing.Bitmap).Assembly, // System.Drawing.dll
                    //					typeof(Form).Assembly, // System.Windows.Forms.dll
                    //					typeof(ICSharpCode.NRefactory.TypeSystem.IProjectContent).Assembly,
                };
            }

            assemblies = assemblies.Where(v => !v.IsDynamic).ToList();

            var unresolvedAssemblies = new IUnresolvedAssembly[assemblies.Count];
            Stopwatch total = Stopwatch.StartNew();
            Parallel.For(
                0, assemblies.Count,
                delegate (int i)
                {
                    var loader = new CecilLoader();
                    var path = assemblies[i].Location;
                    loader.DocumentationProvider = GetXmlDocumentation(assemblies[i].Location);
                    unresolvedAssemblies[i] = loader.LoadAssemblyFile(assemblies[i].Location);
                });
            Debug.WriteLine("Init project content, loading base assemblies: " + total.Elapsed);
            projectContent = projectContent.AddAssemblyReferences((IEnumerable<IUnresolvedAssembly>)unresolvedAssemblies);

            //Earl Testing
            GetReferenceByFiles();
        }



        public CSharpCompletion(ICSharpScriptProvider scriptProvider, IReadOnlyList<Assembly> assemblies = null)
            : this(assemblies)
        {
            ScriptProvider = scriptProvider;
        }

        public ICSharpScriptProvider ScriptProvider { get; set; }

        private XmlDocumentationProvider GetXmlDocumentation(string dllPath)
        {
            if (string.IsNullOrEmpty(dllPath))
                return null;

            var xmlFileName = Path.GetFileNameWithoutExtension(dllPath) + ".xml";
            var localPath = Path.Combine(Path.GetDirectoryName(dllPath), xmlFileName);
            if (File.Exists(localPath))
                return new XmlDocumentationProvider(localPath);

            //if it's a .NET framework assembly it's in one of following folders
            var netPath = Path.Combine(@"C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.0", xmlFileName);
            if (File.Exists(netPath))
                return new XmlDocumentationProvider(netPath);

            return null;
        }

        public void AddAssembly(string file)
        {
            if (String.IsNullOrEmpty(file))
                return;

            var loader = new CecilLoader();
            loader.DocumentationProvider = GetXmlDocumentation(file);
            var unresolvedAssembly = loader.LoadAssemblyFile(file);
            projectContent = projectContent.AddAssemblyReferences(unresolvedAssembly);
        }

        public void ProcessInput(string input, string sourceFile)
        {
            if (string.IsNullOrEmpty(sourceFile))
                return;
            //see if it contains the word class, enum or struct
            //todo: this is buggy because if two classes are evaluated seperately, the original file will overwrite it
            // if the file is a script we should try to extract the class name and use it as the file name. sciptname + class
            // we can probably use the AST for that.
            if (input.Contains("class ") || input.Contains("enum ") || input.Contains("struct "))
            {
                var syntaxTree = new CSharpParser().Parse(input, sourceFile);
                syntaxTree.Freeze();
                var unresolvedFile = syntaxTree.ToTypeSystem();
                projectContent = projectContent.AddOrUpdateFiles(unresolvedFile);
            }
        }

        public CodeCompletionResult GetCompletions(IDocument document, int offset)
        {
            return GetCompletions(document, offset, false);
        }

        public CodeCompletionResult GetCompletions(IDocument document, int offset, bool controlSpace)
        {
            //get the using statements from the script provider
            string usings = null;
            string variables = null;
            string @namespace = null;
            if (ScriptProvider != null)
            {
                usings = ScriptProvider.GetUsing();
                variables = ScriptProvider.GetVars();
                @namespace = ScriptProvider.GetNamespace();
            }
            return GetCompletions(document, offset, controlSpace, usings, variables, @namespace);
        }

        public CodeCompletionResult GetCompletions(IDocument document, int offset, bool controlSpace, string usings, string variables, string @namespace)
        {
            var result = new CodeCompletionResult();
            if (String.IsNullOrEmpty(document.FileName))
                return result;

            var completionContext = new CSharpCompletionContext(document, offset, projectContent, usings, variables, @namespace);
            var completionFactory = new CSharpCompletionDataFactory(completionContext.TypeResolveContextAtCaret, completionContext);
            var cce = new CSharpCompletionEngine(
                completionContext.Document,
                completionContext.CompletionContextProvider,
                completionFactory,
                completionContext.ProjectContent,
                completionContext.TypeResolveContextAtCaret
                );

            cce.EolMarker = Environment.NewLine;
            cce.FormattingPolicy = FormattingOptionsFactory.CreateSharpDevelop();


            var completionChar = completionContext.Document.GetCharAt(completionContext.Offset - 1);
            int startPos, triggerWordLength;
            IEnumerable<ICSharpCode.NRefactory.Completion.ICompletionData> completionData;
            if (controlSpace)
            {
                if (!cce.TryGetCompletionWord(completionContext.Offset, out startPos, out triggerWordLength))
                {
                    startPos = completionContext.Offset;
                    triggerWordLength = 0;
                }
                completionData = cce.GetCompletionData(startPos, true);
                //this outputs tons of available entities
                //if (triggerWordLength == 0)
                //    completionData = completionData.Concat(cce.GetImportCompletionData(startPos));
            }
            else
            {
                startPos = completionContext.Offset;

                if (char.IsLetterOrDigit(completionChar) || completionChar == '_')
                {
                    if (startPos > 1 && char.IsLetterOrDigit(completionContext.Document.GetCharAt(startPos - 2)))
                        return result;
                    completionData = cce.GetCompletionData(startPos, false);
                    startPos--;
                    triggerWordLength = 1;

                }
                else if ((!char.IsLetterOrDigit(completionChar)) || completionChar == '@')
                {

                    var strPrefix = completionContext.Document.GetText(startPos - 5, 4); //Get the Text; FIFO

                    if (strPrefix.Trim().Length > 0 && (strPrefix == Constants.PointCaller || strPrefix == Constants.ModelCaller))
                    {

                        GetPointModuleCompletion(strPrefix, controlSpace, ref result);
                        result.TriggerWord = strPrefix + "@";
                        result.TriggerWordLength = strPrefix.Length + 1;
                        return result;
                    }
                    completionData = cce.GetCompletionData(startPos, false);
                    triggerWordLength = 0;
                }
                else
                {
                    completionData = cce.GetCompletionData(startPos, false);
                    triggerWordLength = 0;
                }
            }

            result.TriggerWordLength = triggerWordLength;
            result.TriggerWord = completionContext.Document.GetText(completionContext.Offset - triggerWordLength, triggerWordLength);
            Debug.Print("Trigger word: '{0}'", result.TriggerWord);

            //cast to AvalonEdit completion data and add to results
            foreach (var completion in completionData)
            {
                var cshellCompletionData = completion as CompletionData;
                if (cshellCompletionData != null)
                {
                    cshellCompletionData.TriggerWord = result.TriggerWord;
                    cshellCompletionData.TriggerWordLength = result.TriggerWordLength;
                    result.CompletionData.Add(cshellCompletionData);
                }
            }

            //method completions
            if (!controlSpace)
            {
                // Method Insight
                var pce = new CSharpParameterCompletionEngine(
                    completionContext.Document,
                    completionContext.CompletionContextProvider,
                    completionFactory,
                    completionContext.ProjectContent,
                    completionContext.TypeResolveContextAtCaret
                );

                var parameterDataProvider = pce.GetParameterDataProvider(completionContext.Offset, completionChar);
                result.OverloadProvider = parameterDataProvider as IOverloadProvider;
            }

            return result;

        }


        private void GetPointModuleCompletion(string prefix, bool controlSpace, ref CodeCompletionResult result)
        {
            var compData = new List<MyCompletionData>();


            if (prefix != null)
            {
                if (prefix == Constants.PointCaller)
                {
                    compData = ClassList.GetPointCompletion() as List<MyCompletionData>;


                }
                else if (prefix == Constants.ModelCaller)
                {
                    compData = ClassList.GetModelCompletion() as List<MyCompletionData>;
                }


                if (controlSpace)
                {
                    foreach (var completion in compData)
                    {
                        var cshellCompletionData = completion as MyCompletionData;
                        cshellCompletionData.PreFix = prefix;
                        if (cshellCompletionData != null)
                        {
                            cshellCompletionData.TriggerWord = result.TriggerWord;
                            cshellCompletionData.TriggerWordLength = result.TriggerWordLength;
                            result.CompletionData.Add(cshellCompletionData);
                        }
                    }
                }
                else
                {
                    foreach (var completion in compData)
                    {
                        var cshellCompletionData = completion as MyCompletionData;
                        cshellCompletionData.PreFix = prefix;
                        if (cshellCompletionData != null)
                        {
                            cshellCompletionData.TriggerWord = prefix + "@";
                            cshellCompletionData.TriggerWordLength = prefix.Length + 1;
                            result.CompletionData.Add(cshellCompletionData);
                        }
                    }


                }


            }
        }



        //Testing
        #region Testing
        private void GetReferenceByFiles()
        {
            var referenceFolderPath = new string[] {
                    "C:\\Users\\earlsan.villegas\\Documents\\PWS",
            };
            //AddReferences(Directory.GetFiles(referenceFolderPath).ToArray());


            foreach (string item in referenceFolderPath)
            {
                AddReferences(Directory.GetFiles(item).ToArray());
            }
            AddAssembly("C:\\Users\\earlsan.villegas\\Documents\\Github\\CodeEditor\\CodeEditor\\CodeEditor\\bin\\Debug\\Dynamic.Points.dll");
        }

        public void AddReferences(params string[] references)
        {
            if (references == null || references.Length == 0)
                return;

            var unresolvedAssemblies = GetUnresolvedAssemblies(references);
            projectContent = projectContent.AddAssemblyReferences((IEnumerable<IUnresolvedAssembly>)unresolvedAssemblies);
        }


        private IUnresolvedAssembly[] GetUnresolvedAssemblies(string[] references)
        {
            IUnresolvedAssembly[] unresolvedAssemblies = null;
            if (references.Length == 1)
            {
                unresolvedAssemblies = new[] { GetUnresolvedAssembly(references[0]) };
            }
            else
            {
                unresolvedAssemblies = references
                    .AsParallel()
                    .AsOrdered()
                    .Select(GetUnresolvedAssembly)
                    .ToArray();
            }
            return unresolvedAssemblies;
        }
        private IUnresolvedAssembly GetUnresolvedAssembly(string reference)
        {
            var fullPath = reference;
            var fileEx = new FileInfo(reference);


            var pathBin = "bin"; //This should be contstant; Contants.BinFolder;
                                 //look in the bin folder
            if (!File.Exists(fullPath))
                fullPath = Path.Combine(Environment.CurrentDirectory, pathBin, reference);
            if (!File.Exists(fullPath))
                fullPath = Path.Combine(Environment.CurrentDirectory, pathBin, reference + ".dll");
            if (!File.Exists(fullPath))
                fullPath = Path.Combine(Environment.CurrentDirectory, pathBin, reference + ".exe");
            //try to resolve as relaive path
            if (!File.Exists(fullPath))
                fullPath = PathHelper.ToAbsolutePath(Environment.CurrentDirectory, reference);
            //exe path
            if (!File.Exists(fullPath))
            {
                string codeBase = Assembly.GetExecutingAssembly().CodeBase;
                UriBuilder uri = new UriBuilder(codeBase);
                string path = Uri.UnescapeDataString(uri.Path);
                var exePath = Path.GetDirectoryName(path);
                if (exePath != null)
                {
                    fullPath = Path.Combine(exePath, reference + ".dll");
                    if (!File.Exists(fullPath))
                        fullPath = Path.Combine(exePath, reference + ".exe");
                }
            }
            //try to find in GAC
            if (!File.Exists(fullPath))
            {
                try
                {
                    var assemblyName = new AssemblyName(reference);
                    fullPath = GlobalAssemblyCache.FindAssemblyInNetGac(assemblyName);
                }
                catch { }
            }
            if (!File.Exists(fullPath))
            {
                var assemblyName = GlobalAssemblyCache.FindBestMatchingAssemblyName(reference);
                if (assemblyName != null)
                    fullPath = GlobalAssemblyCache.FindAssemblyInNetGac(assemblyName);
            }

            if (File.Exists(fullPath))
            {
                var loader = new CecilLoader();
                loader.DocumentationProvider = GetXmlDocumentation(fullPath);
                var unresolvedAssembly = loader.LoadAssemblyFile(fullPath);
                return unresolvedAssembly;
            }
            throw new FileNotFoundException("Reference could not be found: " + reference);

        }


        #endregion

    }
}