#nullable disable // TODO: Enable nullable and review rule
using Microsoft.Dynamics.Nav.CodeAnalysis;
using Microsoft.Dynamics.Nav.CodeAnalysis.Diagnostics;
using Microsoft.Dynamics.Nav.CodeAnalysis.Syntax;
using System.Collections.Immutable;
using BusinessCentral.LinterCop.Helpers;

namespace BusinessCentral.LinterCop.Design;

[DiagnosticAnalyzer]
public class Rule0009CodeMetrics : DiagnosticAnalyzer
{
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        ImmutableArray.Create(DiagnosticDescriptors.Rule0009CodeMetricsInfo, DiagnosticDescriptors.Rule0010CodeMetricsWarning);

    private static readonly HashSet<SyntaxKind> OperatorAndOperandKinds =
        Enum.GetValues(typeof(SyntaxKind))
            .Cast<SyntaxKind>()
            .Where(value =>
                (value.ToString().Contains("Keyword") ||
                 value.ToString().Contains("Token")) ||
                IsOperandKind(value))
            .ToHashSet();

    public override void Initialize(AnalysisContext context) =>
        context.RegisterCodeBlockAction(new Action<CodeBlockAnalysisContext>(this.CheckCodeMetrics));

    private void CheckCodeMetrics(CodeBlockAnalysisContext context)
    {
        if ((context.CodeBlock.Kind != SyntaxKind.MethodDeclaration) &&
            (context.CodeBlock.Kind != SyntaxKind.TriggerDeclaration))
            return;

        if (context.IsObsoletePendingOrRemoved()) return;

        var containingObjectTypeSymbol = context.OwningSymbol.GetContainingObjectTypeSymbol();
        if (containingObjectTypeSymbol.NavTypeKind == NavTypeKind.Interface ||
            containingObjectTypeSymbol.NavTypeKind == NavTypeKind.ControlAddIn)
            return;

        SyntaxNode bodyNode = context.CodeBlock.Kind == SyntaxKind.MethodDeclaration
            ? (context.CodeBlock as MethodDeclarationSyntax)?.Body
            : (context.CodeBlock as TriggerDeclarationSyntax)?.Body;

        if (bodyNode is null)
            return;

        var descendants = bodyNode.DescendantNodesAndTokens(e => true).ToArray();

        if (LinterSettings.instance is null)
            LinterSettings.Create(context.SemanticModel.Compilation.FileSystem.GetDirectoryPath());

        int complexity;
        int complexityThreshold;

        if (LinterSettings.instance.useCognitiveComplexity)
        {
            complexity = GetCognitiveComplexity(descendants);
            complexityThreshold = LinterSettings.instance.cognitiveComplexityThreshold;
        }
        else
        {
            complexity = GetCyclomaticComplexity(descendants);
            complexityThreshold = LinterSettings.instance.cyclomaticComplexityThreshold;
        }

        double HalsteadVolume = GetHalsteadVolume(context, bodyNode, descendants, complexity);

        if (complexity >= complexityThreshold || Math.Round(HalsteadVolume) <= LinterSettings.instance.maintainabilityIndexThreshold)
        {
            context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.Rule0010CodeMetricsWarning, context.OwningSymbol.GetLocation(), new object[] { complexity, complexityThreshold, Math.Round(HalsteadVolume), LinterSettings.instance.maintainabilityIndexThreshold }));
            return;
        }
        context.ReportDiagnostic(Diagnostic.Create(DiagnosticDescriptors.Rule0009CodeMetricsInfo, context.OwningSymbol.GetLocation(), new object[] { complexity, complexityThreshold, Math.Round(HalsteadVolume), LinterSettings.instance.maintainabilityIndexThreshold }));
    }

    private static double GetHalsteadVolume(CodeBlockAnalysisContext context, SyntaxNode methodBodyNode,
        SyntaxNodeOrToken[] descendantNodesAndTokens, int complexity)
    {
        try
        {
            var triviaLinesCount = methodBodyNode
                .DescendantTrivia(e => true, true)
                .Count(node =>
                    node.Kind == SyntaxKind.EndOfLineTrivia &&
                    node.GetLocation().GetLineSpan().StartLinePosition.Line ==
                    node.Token.GetLocation().GetLineSpan().StartLinePosition.Line) - 2; //Minus 2 for Begin end of function

            context.CancellationToken.ThrowIfCancellationRequested();
            var N = 0;
            using var hashSet = PooledHashSet<SyntaxNodeOrToken>.GetInstance();
            foreach (var nodeOrToken in descendantNodesAndTokens)
            {
                if (OperatorAndOperandKinds.Contains(nodeOrToken.Kind))
                {
                    N++;
                    hashSet.Add(nodeOrToken);
                }
            }

            double HalsteadVolume = N * Math.Log(hashSet.Count, 2);

            //171−5.2lnV−0.23G−16.2lnL
            return Math.Max(0, (171 - 5.2 * Math.Log(HalsteadVolume) - 0.23 * complexity - 16.2 * Math.Log(triviaLinesCount)) * 100 / 171);
        }
        catch (System.NullReferenceException)
        {
            return 0.0;
        }
    }

    private static int GetCyclomaticComplexity(SyntaxNodeOrToken[] nodesAndTokens)
    {
        return nodesAndTokens.Count(syntaxNodeOrToken => IsComplexKind(syntaxNodeOrToken.Kind)) + 1;
    }

    private static int GetCognitiveComplexity(SyntaxNodeOrToken[] nodesAndTokens)
    {
        int complexity = 0;
        int nestingLevel = 0;

        foreach (var nodeOrToken in nodesAndTokens)
        {
            switch (nodeOrToken.Kind)
            {
                case SyntaxKind.IfKeyword:
                case SyntaxKind.ElifKeyword:
                case SyntaxKind.ForKeyword:
                case SyntaxKind.ForEachKeyword:
                case SyntaxKind.WhileKeyword:
                case SyntaxKind.UntilKeyword:
                case SyntaxKind.CaseLine:
                    complexity++;
                    nestingLevel++;
                    break;
                case SyntaxKind.LogicalAndExpression:
                case SyntaxKind.LogicalOrExpression:
                    complexity++;
                    break;
                case SyntaxKind.BreakKeyword:
                // case SyntaxKind.ContinueKeyword:
                //     complexity++;
                //     break;
                case SyntaxKind.EndKeyword:
                    nestingLevel--;
                    break;
            }

            complexity += nestingLevel;
        }

        return complexity;
    }

    private static bool IsOperandKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.IdentifierToken:
            case SyntaxKind.Int32LiteralToken:
            case SyntaxKind.StringLiteralToken:
            case SyntaxKind.BooleanLiteralValue:
            case SyntaxKind.TrueKeyword:
            case SyntaxKind.FalseKeyword:
                return true;
        }

        return false;
    }

    private static bool IsComplexKind(SyntaxKind kind)
    {
        switch (kind)
        {
            case SyntaxKind.IfKeyword:
            case SyntaxKind.ElifKeyword:
            case SyntaxKind.LogicalAndExpression:
            case SyntaxKind.LogicalOrExpression:
            case SyntaxKind.CaseLine:
            case SyntaxKind.ForKeyword:
            case SyntaxKind.ForEachKeyword:
            case SyntaxKind.WhileKeyword:
            case SyntaxKind.UntilKeyword:
                return true;
        }

        return false;
    }
}