﻿// 
// TestRefactoringContext.cs
//  
// Author:
//       Mike Krüger <mkrueger@xamarin.com>
// 
// Copyright (c) 2011 Xamarin Inc.
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System;
using System.Collections.Generic;
using System.Linq;
using ICSharpCode.NRefactory.CSharp.Refactoring;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.CSharp.TypeSystem;
using ICSharpCode.NRefactory.Editor;
using ICSharpCode.NRefactory.CSharp.FormattingTests;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.TypeSystem;
using NUnit.Framework;
using System.Threading;
using System.Threading.Tasks;

namespace ICSharpCode.NRefactory.CSharp.CodeActions
{
	public class TestRefactoringContext : RefactoringContext
	{
		public static bool UseExplict {
			get;
			set;
		}

		internal readonly IDocument doc;
		readonly TextLocation location;
		
		public TestRefactoringContext (IDocument document, TextLocation location, CSharpAstResolver resolver) : base(resolver, CancellationToken.None)
		{
			this.doc = document;
			this.location = location;
			this.UseExplicitTypes = UseExplict;
			this.FormattingOptions = FormattingOptionsFactory.CreateMono ();
			UseExplict = false;
			Services.AddService (typeof(NamingConventionService), new TestNameService ());
		}
		
		class TestNameService : NamingConventionService
		{
			public override IEnumerable<NamingRule> Rules {
				get {
					return DefaultRules.GetFdgRules ();
				}
			}
		}
		
		public override bool Supports(Version version)
		{
			return true;
		}
		
		public override TextLocation Location {
			get { return location; }
		}
		
		public CSharpFormattingOptions FormattingOptions { get; set; }
		
		public Script StartScript ()
		{
			return new TestScript (this);
		}
		
		sealed class TestScript : DocumentScript
		{
			readonly TestRefactoringContext context;
			public TestScript(TestRefactoringContext context) : base(context.doc, context.FormattingOptions, new TextEditorOptions ())
			{
				this.context = context;
			}
			
			public override Task Link (params AstNode[] nodes)
			{
				// check that all links are valid.
				foreach (var node in nodes) {
					Assert.IsNotNull (GetSegment (node));
				}
				return new Task (() => {});
			}
			
			public override Task InsertWithCursor(string operation, InsertPosition defaultPosition, IEnumerable<AstNode> nodes)
			{
				EntityDeclaration entity = context.GetNode<EntityDeclaration>();
				if (entity is Accessor) {
					entity = (EntityDeclaration) entity.Parent;
				}

				foreach (var node in nodes) {
					InsertBefore(entity, node);
				}
				var tcs = new TaskCompletionSource<object> ();
				tcs.SetResult (null);
				return tcs.Task;
			}

			public override Task InsertWithCursor (string operation, ITypeDefinition parentType, IEnumerable<AstNode> nodes)
			{
				var unit = context.RootNode;
				var insertType = unit.GetNodeAt<TypeDeclaration> (parentType.Region.Begin);

				var startOffset = GetCurrentOffset (insertType.LBraceToken.EndLocation);
				foreach (var node in nodes.Reverse ()) {
					var output = OutputNode (1, node, true);
					if (parentType.Kind == TypeKind.Enum) {
						InsertText (startOffset, output.Text +",");
					} else {
						InsertText (startOffset, output.Text);
					}
					output.RegisterTrackedSegments (this, startOffset);
				}
				var tcs = new TaskCompletionSource<object> ();
				tcs.SetResult (null);
				return tcs.Task;
			}

			void Rename (AstNode node, string newName)
			{
				if (node is ObjectCreateExpression)
					node = ((ObjectCreateExpression)node).Type;

				if (node is InvocationExpression)
					node = ((InvocationExpression)node).Target;
			
				if (node is MemberReferenceExpression)
					node = ((MemberReferenceExpression)node).MemberNameToken;
			
				if (node is MemberType)
					node = ((MemberType)node).MemberNameToken;
			
				if (node is EntityDeclaration) 
					node = ((EntityDeclaration)node).NameToken;
			
				if (node is ParameterDeclaration) 
					node = ((ParameterDeclaration)node).NameToken;
				if (node is ConstructorDeclaration)
					node = ((ConstructorDeclaration)node).NameToken;
				if (node is DestructorDeclaration)
					node = ((DestructorDeclaration)node).NameToken;
				if (node is VariableInitializer)
					node = ((VariableInitializer)node).NameToken;
				Replace (node, new IdentifierExpression (newName));
			}

			public override void Rename (ISymbol symbol, string name)
			{
				if (symbol.SymbolKind == SymbolKind.Variable || symbol.SymbolKind == SymbolKind.Parameter) {
					Rename(symbol as IVariable, name);
					return;
				}
				FindReferences refFinder = new FindReferences ();
				refFinder.FindReferencesInFile (refFinder.GetSearchScopes (symbol), 
				                               context.UnresolvedFile, 
				                               context.RootNode as SyntaxTree, 
				                               context.Compilation, (n, r) => Rename (n, name), 
				                               context.CancellationToken);
			}

			void Rename (IVariable variable, string name)
			{
				FindReferences refFinder = new FindReferences ();
				refFinder.FindLocalReferences (variable, 
				                               context.UnresolvedFile, 
				                               context.RootNode as SyntaxTree, 
				                               context.Compilation, (n, r) => Rename (n, name), 
				                               context.CancellationToken);
			}
			
			public override void CreateNewType (AstNode newType, NewTypeContext context)
			{
				var output = OutputNode (0, newType, true);
				InsertText (0, output.Text);
			}
		}

		#region Text stuff

		public override bool IsSomethingSelected { get { return selectionStart > 0; }  }

		public override string SelectedText { get { return IsSomethingSelected ? doc.GetText (selectionStart, selectionEnd - selectionStart) : ""; } }
		
		int selectionStart;
		public override TextLocation SelectionStart { get { return doc.GetLocation (selectionStart); } }
		
		int selectionEnd;
		public override TextLocation SelectionEnd { get { return doc.GetLocation (selectionEnd); } }

		public override int GetOffset (TextLocation location)
		{
			return doc.GetOffset (location);
		}
		
		public override TextLocation GetLocation (int offset)
		{
			return doc.GetLocation (offset);
		}

		public override string GetText (int offset, int length)
		{
			return doc.GetText (offset, length);
		}
		
		public override string GetText (ISegment segment)
		{
			return doc.GetText (segment);
		}
		
		public override IDocumentLine GetLineByOffset (int offset)
		{
			return doc.GetLineByOffset (offset);
		}
		#endregion
		public string Text {
			get {
				return doc.Text;
			}
		}
		public static TestRefactoringContext Create (string content, bool expectErrors = false)
		{
			int idx = content.IndexOf ("$");
			if (idx >= 0)
				content = content.Substring (0, idx) + content.Substring (idx + 1);
			int idx1 = content.IndexOf ("<-");
			int idx2 = content.IndexOf ("->");
			
			int selectionStart = 0;
			int selectionEnd = 0;
			if (0 <= idx1 && idx1 < idx2) {
				content = content.Substring (0, idx2) + content.Substring (idx2 + 2);
				content = content.Substring (0, idx1) + content.Substring (idx1 + 2);
				selectionStart = idx1;
				selectionEnd = idx2 - 2;
				idx = selectionEnd;
			}

			var doc = new StringBuilderDocument(content);
			var parser = new CSharpParser();
			var unit = parser.Parse(content, "program.cs");
			if (!expectErrors) {
				if (parser.HasErrors) {
					Console.WriteLine (content);
					Console.WriteLine ("----");
				}
				foreach (var error in parser.Errors) {
					Console.WriteLine(error.Message);
				}
				Assert.IsFalse(parser.HasErrors, "The file contains unexpected parsing errors.");
			} else {
				Assert.IsTrue(parser.HasErrors, "Expected parsing errors, but the file doesn't contain any.");
			}

			unit.Freeze ();
			var unresolvedFile = unit.ToTypeSystem ();
			
			IProjectContent pc = new CSharpProjectContent ();
			pc = pc.AddOrUpdateFiles (unresolvedFile);
			pc = pc.AddAssemblyReferences (new[] { CecilLoaderTests.Mscorlib, CecilLoaderTests.SystemCore });
			
			var compilation = pc.CreateCompilation ();
			var resolver = new CSharpAstResolver (compilation, unit, unresolvedFile);
			TextLocation location = TextLocation.Empty;
			if (idx >= 0)
				location = doc.GetLocation (idx);
			return new TestRefactoringContext(doc, location, resolver) {
				selectionStart = selectionStart,
				selectionEnd = selectionEnd
			};
		}
		
		internal static void Print (AstNode node)
		{
			var v = new CSharpOutputVisitor (Console.Out, FormattingOptionsFactory.CreateMono ());
			node.AcceptVisitor (v);
		}
	}
}
