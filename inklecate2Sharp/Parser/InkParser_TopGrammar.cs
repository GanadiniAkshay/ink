﻿
using System;
using System.Collections.Generic;
using System.Linq;
using Inklewriter.Parsed;

namespace Inklewriter
{
	public partial class InkParser
	{
		// Main entry point
		public Parsed.Story Parse()
		{
			List<Parsed.Object> topLevelContent = StatementsAtLevel (StatementLevel.Top);
            if (hadError) {
                return null;
            }

			Parsed.Story story = new Parsed.Story (topLevelContent);
			return story;
		}

		protected enum StatementLevel
		{
			Stitch,
			Knot,
			Top
		}

		protected List<Parsed.Object> StatementsAtLevel(StatementLevel level)
		{
			return Interleave<Parsed.Object>(
                Optional (MultilineWhitespace), 
                () => StatementAtLevel (level), 
                untilTerminator: () => StatementsBreakForLevel(level));
		}

		protected object StatementAtLevel(StatementLevel level)
		{
			List<ParseRule> rulesAtLevel = new List<ParseRule> ();


            // Diverts can go anywhere
            // (Check before KnotDefinition since possible "==>" has to be found before "== name ==")
            rulesAtLevel.Add(Line(Divert));

			if (level >= StatementLevel.Top) {

				// Knots can only be parsed at Top/Global scope
				rulesAtLevel.Add (KnotDefinition);
			}

            // Error checking for Choices in the wrong place is below (after parsing)
			rulesAtLevel.Add(Line(Choice));
            rulesAtLevel.Add (GatherLine);

            // Stitches (and gathers) can (currently) only go in Knots and top level
			if (level >= StatementLevel.Knot) {
				rulesAtLevel.Add (StitchDefinition);
			}

            // Normal logic / text can go anywhere
			rulesAtLevel.Add (LogicLine);
			rulesAtLevel.Add(LineOfMixedTextAndLogic);

            // Parse the rules
			var statement = OneOf (rulesAtLevel.ToArray());

            // For some statements, allow them to parse, but create errors, since
            // writers may think they can use the statement, so it's useful to have 
            // the error message.
            if (level == StatementLevel.Top) {

                if( statement is Return ) 
                    Error ("should not have return statement outside of a knot");

                if (statement is Choice)
                    Error ("choices can only be in knots and stitches");

                if (statement is Gather)
                    Error ("gather points can only be in knots and stitches");
            }

			if (statement == null) {
				return null;
			}

			return statement;
		}

        protected object StatementsBreakForLevel(StatementLevel level)
        {
            BeginRule ();

            Whitespace ();

            var breakingRules = new List<ParseRule> ();

            // Break current knot with a new knot
            if (level <= StatementLevel.Knot) {
                breakingRules.Add (KnotTitleEquals);
            }

            // Break current stitch with a new stitch
            if (level <= StatementLevel.Stitch) {
                breakingRules.Add (String("="));
            }

            var breakRuleResult = OneOf (breakingRules.ToArray ());
            if (breakRuleResult == null) {
                return FailRule ();
            }

            return SucceedRule (breakRuleResult);
        }

		protected object SkipToNextLine()
		{
			ParseUntilCharactersFromString ("\n\r");
			ParseNewline ();
			return ParseSuccess;
		}

		// Modifier to turn a rule into one that expects a newline on the end.
		// e.g. anywhere you can use "MixedTextAndLogic" as a rule, you can use 
		// "Line(MixedTextAndLogic)" to specify that it expects a newline afterwards.
		protected ParseRule Line(ParseRule inlineRule)
		{
			return () => {
				var result = inlineRule();
				if( result == null ) {
					return null;
				}

				Expect(EndOfLine, "end of line", recoveryRule: SkipToNextLine);

				return result;
			};
		}

        const string knotDivertArrow = "==>";
        const string stitchDivertArrow = "=>";
        const string weavePointDivertArrow = "->";

		protected Parsed.Divert Divert()
		{
			BeginRule ();

			Whitespace ();

            var knotName = DivertTargetWithArrow (knotDivertArrow);
            var stitchName = DivertTargetWithArrow (stitchDivertArrow);
            var weavePointName = DivertTargetWithArrow (weavePointDivertArrow);
            if (knotName == null && stitchName == null && weavePointName == null) {
                return (Parsed.Divert)FailRule ();
            }

            Whitespace ();

            var optionalArguments = ExpressionFunctionCallArguments ();

            Path targetPath = Path.To(knotName, stitchName, weavePointName);

            return SucceedRule( new Divert(targetPath, optionalArguments) ) as Divert;
		}

        string DivertTargetWithArrow(string arrowStr)
        {
            BeginRule ();

            Whitespace ();

            if (ParseString (arrowStr) == null)
                return (string)FailRule ();

            Whitespace ();

            var targetName = Expect(Identifier, "name of target to divert to");

            return (string) SucceedRule (targetName);
        }

		protected string DivertArrow()
		{
            return OneOf(String(knotDivertArrow), String(stitchDivertArrow), String(weavePointDivertArrow)) as string;
		}



		protected string Identifier()
		{
			if (_identifierCharSet == null) {

                _identifierFirstCharSet = new CharacterSet ();
                _identifierFirstCharSet.AddRange ('A', 'Z');
                _identifierFirstCharSet.AddRange ('a', 'z');
                _identifierFirstCharSet.AddRange ('0', '9');
                _identifierFirstCharSet.Add ('_');

                _identifierCharSet = new CharacterSet(_identifierFirstCharSet);
				_identifierCharSet.AddRange ('0', '9');
			}

            BeginRule ();

            // Parse single character first
            var name = ParseCharactersFromCharSet (_identifierFirstCharSet, true, 1);
            if (name == null) {
                return (string) FailRule ();
            }

            // Parse remaining characters (if any)
            var tailChars = ParseCharactersFromCharSet (_identifierCharSet);
            if (tailChars != null) {
                name = name + tailChars;
            }

            return (string) SucceedRule(name);
		}
        private CharacterSet _identifierFirstCharSet;
		private CharacterSet _identifierCharSet;


		protected Parsed.Object LogicLine()
		{
			BeginRule ();

			Whitespace ();

			if (ParseString ("~") == null) {
				return FailRule () as Parsed.Object;
			}

			Whitespace ();

            // Some example lines we need to be able to distinguish between:
            // ~ var x = 5  -- var decl + assign
            // ~ var x      -- var decl
            // ~ x = 5      -- var assign
            // ~ x          -- expr (not var decl or assign)
            // ~ f()        -- expr
            // We don't treat variable decl/assign as an expression since we don't want an assignment
            // to have a return value, or to be used in compound expressions.
            ParseRule afterTilda = () => OneOf (ReturnStatement, VariableDeclarationOrAssignment, Expression);

            var parsedExpr = (Parsed.Object) Expect(afterTilda, "expression after '~'", recoveryRule: SkipToNextLine);

			// TODO: A piece of logic after a tilda shouldn't have its result printed as text (I don't think?)
            return SucceedRule (parsedExpr) as Parsed.Object;
		}

        protected List<Parsed.Object> LineOfMixedTextAndLogic()
        {
            BeginRule ();

            var result = MixedTextAndLogic();
            if (result == null || result.Count == 0) {
                return (List<Parsed.Object>) FailRule();
            }

            // Trim whitepace from start
            var firstText = result[0] as Text;
            if (firstText != null) {
                firstText.content = firstText.content.TrimStart(' ', '\t');
                if (firstText.content.Length == 0) {
                    result.RemoveAt (0);
                }
            }
            if (result.Count == 0) {
                return (List<Parsed.Object>) FailRule();
            }

            // Trim whitespace from end and add a newline
            var lastObj = result.Last ();
            if (lastObj is Text) {
                var text = (Text)lastObj;
                text.content = text.content.TrimEnd (' ', '\t') + "\n";
            } 

            // Last object in line wasn't text (but some kind of logic), so
            // we need to append the newline afterwards using a new object
            // TODO: Under what conditions should we NOT do this?
            else {
                result.Add (new Text ("\n"));
            }

            Expect(EndOfLine, "end of line", recoveryRule: SkipToNextLine);

            return (List<Parsed.Object>) SucceedRule(result);
        }

		protected List<Parsed.Object> MixedTextAndLogic()
		{
			// Either, or both interleaved
			return Interleave<Parsed.Object>(Optional (ContentText), Optional (InlineLogic));
		}

		protected Parsed.Object InlineLogic()
		{
			BeginRule ();

			if ( ParseString ("{") == null) {
				return FailRule () as Parsed.Object;
			}

			Whitespace ();

            var logic = Expect(InnerLogic, "inner logic within '{' and '}' braces");
            if (logic == null) {
                return (Parsed.Object) FailRule ();
            }


			Whitespace ();

            Expect (String("}"), "closing brace '}' for inline logic");

			return SucceedRule(logic) as Parsed.Object;
		}

        protected Parsed.Object InnerLogic()
        {
            return (Parsed.Object) OneOf (InnerConditionalContent, InnerExpression);
        }

        protected Conditional InnerConditionalContent()
        {
            BeginRule ();

            var expr = Expression ();
            if (expr == null) {
                return (Conditional) FailRule ();
            }

            Whitespace ();

            if (ParseString (":") == null)
                return (Conditional) FailRule ();

            List<List<Parsed.Object>> alternatives;

            // Multi-line conditional
            if (Newline () != null) {
                alternatives = (List<List<Parsed.Object>>) Expect (MultilineConditionalOptions, "conditional branches on following lines");
            } 

            // Inline conditional
            else {
                alternatives = Interleave<List<Parsed.Object>>(MixedTextAndLogic, Exclude (String ("|")), flatten:false);
            }

            if (alternatives == null || alternatives.Count < 1 || alternatives.Count > 2) {
                Error ("Expected one or two alternatives separated by '|' in inline conditional");
                return (Conditional)FailRule ();
            }

            List<Parsed.Object> contentIfTrue = alternatives [0];
            List<Parsed.Object> contentIfFalse = null;
            if (alternatives.Count > 1) {
                contentIfFalse = alternatives [1];
            }

            var cond = new Conditional (expr, contentIfTrue, contentIfFalse);

            return (Conditional) SucceedRule(cond);
        }

        protected List<List<Parsed.Object>> MultilineConditionalOptions()
        {
            return OneOrMore (IndividualConditionBranchLine).Cast<List<Parsed.Object>>().ToList();
        }

        protected List<Parsed.Object> IndividualConditionBranchLine()
        {
            BeginRule ();

            Whitespace ();

            if (ParseString ("-") == null)
                return (List<Parsed.Object>) FailRule ();

            Whitespace ();

            List<Parsed.Object> content = LineOfMixedTextAndLogic ();

            return (List<Parsed.Object>) SucceedRule (content);
        }

		protected Parsed.Object InnerExpression()
		{
            var expr = Expression ();
            if (expr != null) {
                expr.outputWhenComplete = true;
            }
            return expr;
		}

		// Content text is an unusual parse rule compared with most since it's
		// less about saying "this is is the small selection of stuff that we parse"
		// and more "we parse ANYTHING except this small selection of stuff".
		protected Parsed.Text ContentText()
		{
            BeginRule ();

			// Eat through text, pausing at the following characters, and
			// attempt to parse the nonTextRule.
			// "-": possible start of divert or start of gather
			if (_nonTextPauseCharacters == null) {
				_nonTextPauseCharacters = new CharacterSet ("-");
			}

			// If we hit any of these characters, we stop *immediately* without bothering to even check the nonTextRule
            // "{" for start of logic
            // "=" for start of divert or new stitch
			if (_nonTextEndCharacters == null) {
                _nonTextEndCharacters = new CharacterSet ("={}|\n\r");
			}

			// When the ParseUntil pauses, check these rules in case they evaluate successfully
			ParseRule nonTextRule = () => OneOf (DivertArrow, EndOfLine);
			
			string pureTextContent = ParseUntil (nonTextRule, _nonTextPauseCharacters, _nonTextEndCharacters);
			if (pureTextContent != null ) {
                return (Parsed.Text) SucceedRule( new Parsed.Text (pureTextContent) );

			} else {
                return (Parsed.Text) FailRule();
			}

		}
		private CharacterSet _nonTextPauseCharacters;
		private CharacterSet _nonTextEndCharacters;
	}
}

