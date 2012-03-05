﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using ICSharpCode.NRefactory.CSharp;
using ICSharpCode.NRefactory.CSharp.Resolver;
using ICSharpCode.NRefactory.Semantics;
using ICSharpCode.NRefactory.TypeSystem;
using Saltarelle.Compiler.JSModel.Expressions;
using Saltarelle.Compiler.JSModel.Statements;

namespace Saltarelle.Compiler {
	public class StatementCompiler : DepthFirstAstVisitor {
		private readonly INamingConventionResolver _namingConvention;
		private readonly IErrorReporter _errorReporter;
		private readonly ICompilation _compilation;
		private readonly CSharpAstResolver _resolver;
		private readonly IDictionary<IVariable, VariableData> _variables;
		private readonly IDictionary<LambdaResolveResult, NestedFunctionData> _nestedFunctions;
		private readonly ExpressionCompiler _expressionCompiler;

		private List<JsStatement> _result;

		public StatementCompiler(INamingConventionResolver namingConvention, IErrorReporter errorReporter, ICompilation compilation, CSharpAstResolver resolver, IDictionary<IVariable, VariableData> variables, IDictionary<LambdaResolveResult, NestedFunctionData> nestedFunctions) {
			_namingConvention   = namingConvention;
			_errorReporter      = errorReporter;
			_compilation        = compilation;
			_resolver           = resolver;
			_variables          = variables;
			_nestedFunctions    = nestedFunctions;
			_expressionCompiler = new ExpressionCompiler(_namingConvention, _variables);
			_result             = new List<JsStatement>();
		}

		private StatementCompiler CreateInnerCompiler() {
			return new StatementCompiler(_namingConvention, _errorReporter, _compilation, _resolver, _variables, _nestedFunctions);
		}

		public JsBlockStatement Compile(Statement statement) {
			statement.AcceptVisitor(this);
			if (_result.Count == 1 && _result[0] is JsBlockStatement)
				return (JsBlockStatement)_result[0];
			else
				return new JsBlockStatement(_result);
		}

		private ExpressionCompiler.Result CompileExpression(Expression expr, bool returnValueIsImportant) {
			return _expressionCompiler.Compile(_resolver.Resolve(expr), returnValueIsImportant);
		}

		public override void VisitComment(Comment comment) {
			switch (comment.CommentType) {
				case CommentType.SingleLine: {
					_result.Add(new JsComment(comment.Content));
					break;
				}

				case CommentType.MultiLine: {
					string prefix = new Regex(@"^\s*").Match(comment.Content).Captures[0].Value;
					List<string> commentLines = comment.Content.Replace("\r", "").Split('\n').Select(item => item.Trim()).SkipWhile(l => l == "").ToList();
					while (commentLines.Count > 0 && commentLines[commentLines.Count - 1] == "")
						commentLines.RemoveAt(commentLines.Count - 1);

					if (commentLines.Count > 0)
						_result.Add(new JsComment(string.Join(Environment.NewLine, commentLines.Select(item => prefix + item))));	// Replace the space at the start of each line with the same as the space in the first line.
					break;
				}
					
				case CommentType.Documentation:
				case CommentType.MultiLineDocumentation:
					// Better to use the NRefactory XML support if we want these.
					break;
				case CommentType.InactiveCode:
					// Should not appear in script.
					break;
				default:
					throw new ArgumentException("Invalid comment type " + comment.CommentType);
			}
		}

		public override void VisitVariableDeclarationStatement(VariableDeclarationStatement variableDeclarationStatement) {
			var declarations = new List<JsVariableDeclaration>();
			foreach (var d in variableDeclarationStatement.Variables) {
				var data = _variables[((LocalResolveResult)_resolver.Resolve(d)).Variable];
				JsExpression initializer;
				if (!d.Initializer.IsNull) {
					var exprCompileResult = CompileExpression(d.Initializer, false);
					if (exprCompileResult.AdditionalStatements.Count > 0) {
						if (declarations.Count > 0) {
							_result.Add(new JsVariableDeclarationStatement(declarations));
							declarations = new List<JsVariableDeclaration>();
						}
						foreach (var s in exprCompileResult.AdditionalStatements) {
							_result.Add(s);
						}
					}
					initializer = (data.UseByRefSemantics ? JsExpression.ObjectLiteral(new[] { new JsObjectLiteralProperty("$", exprCompileResult.Expression) }) : exprCompileResult.Expression);
				}
				else {
					if (data.UseByRefSemantics)
						initializer = JsExpression.ObjectLiteral(new[] { new JsObjectLiteralProperty("$", JsExpression.Null) });
					else
						initializer = null;
				}
				declarations.Add(new JsVariableDeclaration(data.Name, initializer));
			}

			if (declarations.Count > 0)
				_result.Add(new JsVariableDeclarationStatement(declarations));

			base.VisitVariableDeclarationStatement(variableDeclarationStatement);
		}

		public override void VisitExpressionStatement(ExpressionStatement expressionStatement) {
			var compiled = CompileExpression(expressionStatement.Expression, false);
			_result.AddRange(compiled.AdditionalStatements);
			_result.Add(new JsExpressionStatement(compiled.Expression));
		}

		public override void VisitForStatement(ForStatement forStatement) {
			// Initializer. In case we need more than one statement, put all other statements just before this loop.
			JsStatement initializer;
			if (forStatement.Initializers.Count == 1 && forStatement.Initializers.First() is VariableDeclarationStatement) {
				forStatement.Initializers.First().AcceptVisitor(this);
				initializer = _result[_result.Count - 1];
				_result.RemoveAt(_result.Count - 1);
			}
			else {
				JsExpression initExpr = null;
				foreach (var init in forStatement.Initializers) {
					var compiledInit = CompileExpression(((ExpressionStatement)init).Expression, false);
					if (compiledInit.AdditionalStatements.Count == 0) {
						initExpr = (initExpr != null ? JsExpression.Comma(initExpr, compiledInit.Expression) : compiledInit.Expression);
					}
					else {
						if (initExpr != null)
							_result.Add(new JsExpressionStatement(initExpr));
						_result.AddRange(compiledInit.AdditionalStatements);
						initExpr = compiledInit.Expression;
					}
				}
				initializer = (initExpr != null ? (JsStatement)new JsExpressionStatement(initExpr) : (JsStatement)new JsEmptyStatement());
			}

			// Condition
			JsExpression condition;
			List<JsStatement> preBody = null;
			if (!forStatement.Condition.IsNull) {
				var compiledCondition = CompileExpression(forStatement.Condition, true);
				if (compiledCondition.AdditionalStatements.Count == 0) {
					condition = compiledCondition.Expression;
				}
				else {
					// The condition requires additional statements. Transform "for (int i = 0; i < (SomeProperty = 1); i++) { ... }" to "for (var i = 0;; i++) { this.set_SomeProperty(1); if (!(i < 1)) { break; } ... }
					preBody = new List<JsStatement>();
					preBody.AddRange(compiledCondition.AdditionalStatements);
					preBody.Add(new JsIfStatement(JsExpression.LogicalNot(compiledCondition.Expression), new JsBreakStatement(), null));
					condition = null;
				}
			}
			else {
				condition = null;
			}

			// Iterators
			JsExpression iterator = null;
			List<JsStatement> postBody = null;
			if (forStatement.Iterators.Count > 0) {
				var compiledIterators = forStatement.Iterators.Select(i => CompileExpression(((ExpressionStatement)i).Expression, false)).ToList();
				if (compiledIterators.All(i => i.AdditionalStatements.Count == 0)) {
					// No additional statements are required, add them as a single comma-separated expression to the JS iterator.
					iterator = compiledIterators.Aggregate(iterator, (current, i) => (current != null ? JsExpression.Comma(current, i.Expression) : i.Expression));
				}
				else {
					// At least one of the compiled iterators need additional statements. We could add the last expressions that don't need extra statements to the iterators section of the for loop, but for simplicity we'll just add everything to the end of the loop.
					postBody = new List<JsStatement>();
					foreach (var i in compiledIterators) {
						postBody.AddRange(i.AdditionalStatements);
						postBody.Add(new JsExpressionStatement(i.Expression));
					}
				}
			}

			// Body
			var body = CreateInnerCompiler().Compile(forStatement.EmbeddedStatement);

			if (preBody != null || postBody != null) {
				body = new JsBlockStatement(((IEnumerable<JsStatement>)preBody ?? new JsStatement[0]).Concat(body.Statements).Concat((IEnumerable<JsStatement>)postBody ?? new JsStatement[0]));
			}

			_result.Add(new JsForStatement(initializer, condition, iterator, body));
		}

		public override void VisitBreakStatement(BreakStatement breakStatement) {
			_result.Add(new JsBreakStatement());
		}

		public override void VisitContinueStatement(ContinueStatement continueStatement) {
			_result.Add(new JsContinueStatement());
		}

		public override void VisitEmptyStatement(EmptyStatement emptyStatement) {
			_result.Add(new JsEmptyStatement());
		}

		public override void VisitIfElseStatement(IfElseStatement ifElseStatement) {
			var compiledCond = CompileExpression(ifElseStatement.Condition, true);
			_result.AddRange(compiledCond.AdditionalStatements);
			_result.Add(new JsIfStatement(compiledCond.Expression, CreateInnerCompiler().Compile(ifElseStatement.TrueStatement), !ifElseStatement.FalseStatement.IsNull ? CreateInnerCompiler().Compile(ifElseStatement.FalseStatement) : null));
		}

		public override void VisitBlockStatement(BlockStatement blockStatement) {
			var innerCompiler = CreateInnerCompiler();
			foreach (var c in blockStatement.Children)
				c.AcceptVisitor(innerCompiler);
			_result.Add(new JsBlockStatement(innerCompiler._result));
		}

		public override void VisitCheckedStatement(CheckedStatement checkedStatement) {
			checkedStatement.Body.AcceptVisitor(this);
		}

		public override void VisitUncheckedStatement(UncheckedStatement uncheckedStatement) {
			uncheckedStatement.Body.AcceptVisitor(this);
		}

		public override void VisitDoWhileStatement(DoWhileStatement doWhileStatement) {
			var body = CreateInnerCompiler().Compile(doWhileStatement.EmbeddedStatement);
			var compiledCondition = CompileExpression(doWhileStatement.Condition, true);
			if (compiledCondition.AdditionalStatements.Count > 0)
				body = new JsBlockStatement(body.Statements.Concat(compiledCondition.AdditionalStatements));
			_result.Add(new JsDoWhileStatement(compiledCondition.Expression, body));
		}

		public override void VisitWhileStatement(WhileStatement whileStatement) {
			// Condition
			JsExpression condition;
			List<JsStatement> preBody = null;
			var compiledCondition = CompileExpression(whileStatement.Condition, true);
			if (compiledCondition.AdditionalStatements.Count == 0) {
				condition = compiledCondition.Expression;
			}
			else {
				// The condition requires additional statements. Transform "while ((SomeProperty = 1) < 0) { ... }" to "while (true) { this.set_SomeProperty(1); if (!(i < 1)) { break; } ... }
				preBody = new List<JsStatement>();
				preBody.AddRange(compiledCondition.AdditionalStatements);
				preBody.Add(new JsIfStatement(JsExpression.LogicalNot(compiledCondition.Expression), new JsBreakStatement(), null));
				condition = JsExpression.True;
			}

			var body = CreateInnerCompiler().Compile(whileStatement.EmbeddedStatement);
			if (preBody != null)
				body = new JsBlockStatement(preBody.Concat(body.Statements));

			_result.Add(new JsWhileStatement(condition, body));
		}

		public override void VisitReturnStatement(ReturnStatement returnStatement) {
			if (!returnStatement.Expression.IsNull) {
				var expr = CompileExpression(returnStatement.Expression, true);
				_result.AddRange(expr.AdditionalStatements);
				_result.Add(new JsReturnStatement(expr.Expression));
			}
			else {
				_result.Add(new JsReturnStatement());
			}
		}

		public override void VisitLockStatement(LockStatement lockStatement) {
			var expr = CompileExpression(lockStatement.Expression, false);
			_result.AddRange(expr.AdditionalStatements);
			_result.Add(new JsExpressionStatement(expr.Expression));
			lockStatement.EmbeddedStatement.AcceptVisitor(this);
		}

		public override void VisitFixedStatement(FixedStatement fixedStatement) {
			throw new NotImplementedException();
		}

		public override void VisitForeachStatement(ForeachStatement foreachStatement) {
			throw new NotImplementedException();
		}

		public override void VisitGotoCaseStatement(GotoCaseStatement gotoCaseStatement) {
			throw new NotImplementedException();
		}

		public override void VisitGotoDefaultStatement(GotoDefaultStatement gotoDefaultStatement) {
			throw new NotImplementedException();
		}

		public override void VisitGotoStatement(GotoStatement gotoStatement) {
			throw new NotImplementedException();
		}

		public override void VisitLabelStatement(LabelStatement labelStatement) {
			throw new NotImplementedException();
		}

		public override void VisitSwitchStatement(SwitchStatement switchStatement) {
			throw new NotImplementedException();
		}

		public override void VisitThrowStatement(ThrowStatement throwStatement) {
			throw new NotImplementedException();
		}

		public override void VisitTryCatchStatement(TryCatchStatement tryCatchStatement) {
			throw new NotImplementedException();
		}

		public override void VisitUnsafeStatement(UnsafeStatement unsafeStatement) {
			throw new NotImplementedException();
		}

		public override void VisitUsingStatement(UsingStatement usingStatement) {
			throw new NotImplementedException();
		}

		public override void VisitYieldBreakStatement(YieldBreakStatement yieldBreakStatement) {
			throw new NotImplementedException();
		}

		public override void VisitYieldReturnStatement(YieldReturnStatement yieldReturnStatement) {
			throw new NotImplementedException();
		}
	}
}
