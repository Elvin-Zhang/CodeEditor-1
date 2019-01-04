﻿
using AvalonEdit.AddIn.Util;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.NRefactory.Editor;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Input;

namespace AvalonEdit.AddIn
{
    public class CodeTextEditor : TextEditor
    {
        protected CompletionWindow completionWindow;
        protected OverloadInsightWindow insightWindow;


        public CSharpCompletion Completion { get; set; }
        BracketHighlightRenderer bracketRenderer;

        public CodeTextEditor() 
        {

            bracketRenderer = new BracketHighlightRenderer(this.TextArea.TextView);
            TextArea.TextEntering += OnTextEntering;
            TextArea.TextEntered += OnTextEntered;
            TextArea.Caret.PositionChanged += HighlightBrackets;
            ShowLineNumbers = true;


            //Bracket Matchint Higlighting
            UpdateBracketHighlighting();

            var ctrlSpace = new RoutedCommand();
            ctrlSpace.InputGestures.Add(new KeyGesture(Key.Space, ModifierKeys.Control));

            var pressCtrlF1 = new RoutedCommand();
            pressCtrlF1.InputGestures.Add(new KeyGesture(Key.F1, ModifierKeys.Control, "Testing"));


            var cb = new CommandBinding(ctrlSpace, OnCtrlSpaceCommand);

            this.CommandBindings.Add(cb);
            this.CommandBindings.Add(new CommandBinding(pressCtrlF1, OnCtrlF1));

        }

        private void OnCtrlF1(object sender, ExecutedRoutedEventArgs e)
        {
            //throw new NotImplementedException();
            //CompletionWindow win = new CompletionWindow(TextArea);
            //IList<ICompletionData> complist = win.CompletionList.CompletionData;
            //complist.Add(new MyCompletionData("Point1"));
            //complist.Add(new MyCompletionData("Point2"));
            //complist.Add(new MyCompletionData("Point3"));
            //complist.Add(new MyCompletionData("Point4"));
            //complist.Add(new MyCompletionData("Point5"));
            //complist.Add(new MyCompletionData("Point6"));
            //complist.Add(new MyCompletionData("Point7"));
            //win.Show();

        }

        #region CaretPositionChanged - Bracket Highlighting
        /// <summary>
        /// Highlights matching brackets.
        /// </summary>
        void HighlightBrackets(object sender, EventArgs e)
        {
            try
            {
                var doc = new ReadOnlyDocument(new StringTextSource(Text), FileName);
                var bracketSearchResult = new CSharpBracketSearcher().SearchBracket(doc, TextArea.Caret.Offset);
                bracketRenderer.SetHighlight(bracketSearchResult);
            }
            catch (Exception exception)
            {

                Debug.WriteLine("Error in getting the BracketSearcher: " + exception);
            }

        }
        #endregion

        #region CodeCompletion
        private void OnTextEntering(object sender, TextCompositionEventArgs textCompositionEventArgs)
        {
            Debug.WriteLine("TextEntering: " + textCompositionEventArgs.Text);
            if (textCompositionEventArgs.Text.Length > 0 && completionWindow != null)
            {
                if (!char.IsLetterOrDigit(textCompositionEventArgs.Text[0]))
                {
                    // Whenever a non-letter is typed while the completion window is open,
                    // insert the currently selected element.
                    completionWindow.CompletionList.RequestInsertion(textCompositionEventArgs);
                }
            }
            // Do not set e.Handled=true.
            // We still want to insert the character that was typed.
        }
        private void OnTextEntered(object sender, TextCompositionEventArgs textCompositionEventArgs)
        {
            ShowCompletion(textCompositionEventArgs.Text, false);
        }
        private void OnCtrlSpaceCommand(object sender, ExecutedRoutedEventArgs executedRoutedEventArgs)
        {
            ShowCompletion(null, true);
        }
        private void ShowCompletion(string enteredText, bool controlSpace)

        {
            if (!controlSpace)
                Debug.WriteLine("Code Completion: TextEntered: " + enteredText);
            else
                Debug.WriteLine("Code Completion: Ctrl+Space");

            //only process csharp files and if there is a code completion engine available
            if (String.IsNullOrEmpty(Document.FileName))
            {
                Debug.WriteLine("No document file name, cannot run code completion");
                return;
            }


            if (Completion == null)
            {
                Debug.WriteLine("Code completion is null, cannot run code completion");
                return;
            }

            var fileExtension = Path.GetExtension(Document.FileName);
            fileExtension = fileExtension != null ? fileExtension.ToLower() : null;
            //check file extension to be a c# file (.cs, .csx, etc.)
            if (fileExtension == null || (!fileExtension.StartsWith(".cs")))
            {
                Debug.WriteLine("Wrong file extension, cannot run code completion");
                return;
            }

            if (completionWindow == null)
            {
                CodeCompletionResult results = null;
                try
                {
                    var offset = 0;
                    var doc = GetCompletionDocument(out offset);
                    results = Completion.GetCompletions(doc, offset, controlSpace);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine("Error in getting completion: " + exception);
                }
                if (results == null)
                    return;

                if (insightWindow == null && results.OverloadProvider != null)
                {
                    insightWindow = new OverloadInsightWindow(TextArea);
                    insightWindow.Provider = results.OverloadProvider;
                    insightWindow.Show();
                    insightWindow.Closed += (o, args) => insightWindow = null;
                    return;
                }

                if (completionWindow == null && results != null && results.CompletionData.Any())
                {
                    // Open code completion after the user has pressed dot:
                    completionWindow = new CompletionWindow(TextArea);
                    completionWindow.CloseWhenCaretAtBeginning = controlSpace;
                    completionWindow.StartOffset -= results.TriggerWordLength;
                    //completionWindow.EndOffset -= results.TriggerWordLength;

                    IList<ICompletionData> data = completionWindow.CompletionList.CompletionData;
                    foreach (var completion in results.CompletionData.OrderBy(item => item.Text))
                    {
                        data.Add(completion);
                    }
                    if (results.TriggerWordLength > 0)
                    {
                        //completionWindow.CompletionList.IsFiltering = false;
                        if (results.TriggerWord != string.Concat(Constants.PointCaller, "@") && results.TriggerWord != string.Concat(Constants.ModelCaller, "@"))
                        {
                            completionWindow.CompletionList.SelectItem(results.TriggerWord);
                        }
                        
                    }
                    completionWindow.Show();
                    completionWindow.Closed += (o, args) => completionWindow = null;
                }
            }//end if


            //update the insight window
            if (!string.IsNullOrEmpty(enteredText) && insightWindow != null)
            {
                //whenver text is entered update the provider
                var provider = insightWindow.Provider as CSharpOverloadProvider;
                if (provider != null)
                {
                    //since the text has not been added yet we need to tread it as if the char has already been inserted
                    var offset = 0;
                    var doc = GetCompletionDocument(out offset);
                    provider.Update(doc, offset);
                    //if the windows is requested to be closed we do it here
                    if (provider.RequestClose)
                    {
                        insightWindow.Close();
                        insightWindow = null;
                    }
                }
            }
        }//end method


        #endregion


        #region Open & Save File
        public string FileName { get; set; }


        public void OpenFile(string fileName)
        {
            if (!System.IO.File.Exists(fileName))
                throw new FileNotFoundException(fileName);

            if (completionWindow != null)
                completionWindow.Close();
            if (insightWindow != null)
                insightWindow.Close();

            FileName = fileName;
            Load(fileName);
            Document.FileName = FileName;

            SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(fileName));
        }

        public void NewFile(string fileName, string text)
        {
            if (completionWindow != null)
                completionWindow.Close();
            if (insightWindow != null)
                insightWindow.Close();

            FileName = fileName;
            Text = text;
            Document.FileName = FileName;

            SyntaxHighlighting = HighlightingManager.Instance.GetDefinitionByExtension(Path.GetExtension(fileName));
        }

        public bool SaveFile()
        {
            if (String.IsNullOrEmpty(FileName))
                return false;

            Save(FileName);
            return true;
        }
        #endregion


        protected virtual IDocument GetCompletionDocument(out int offset)
        {
            offset = CaretOffset;
            return new ReadOnlyDocument(new StringTextSource(Text), FileName); ;
        }


        private void UpdateBracketHighlighting()
        {
            BracketHighlightRenderer.ApplyCustomizationsToRendering(this.bracketRenderer);
            TextArea.TextView.Redraw();
        }

    }
}
