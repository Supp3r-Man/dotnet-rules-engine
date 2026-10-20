using System.ComponentModel;
using System.Linq.Expressions;
using System.Text.RegularExpressions;

namespace RulesEngine;

/// <summary>Knobs for compiling rules. Instances are immutable; share one per application.</summary>
public sealed class RuleOptions
{
    public static RuleOptions Default { get; } = new();

    /// <summary>Clock used by now() and daysSince(). Inject a fixed one in tests.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>Per-match timeout for the 'matches' operator (default 250 ms).</summary>
    public TimeSpan RegexTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Maximum nesting depth of a rule (parentheses, prefix operators, tree height).</summary>
    public int MaxDepth { get; init; } = Parser.DefaultMaxDepth;

    /// <summary>
    /// Emit the parameterless string.StartsWith/EndsWith(string) overloads, which EF Core can translate to SQL.
    /// Those overloads are CULTURE-SENSITIVE in memory, so leave this off for in-memory evaluation.
    /// </summary>
    public bool QueryFriendly { get; init; }

    internal void Validate()
    {
        if (RegexTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RegexTimeout), "the regex timeout must be positive");
        }

        if (MaxDepth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        }
    }
}

/// <summary>A clock frozen at one instant, for tests and reproducible demos.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
}

/// <summary>Entry points: text -> AST -> bound tree -> expression tree -> delegate.</summary>
public static class RuleCompiler
{
    /// <summary>The complete list of callable functions. There is no other way to call anything.</summary>
    public static IReadOnlyList<string> AllowedFunctions => Binder.FunctionNames;

    /// <summary>Compiles a boolean rule against <typeparamref name="T"/>.</summary>
    public static CompiledRule<T> Compile<T>(string source, RuleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= RuleOptions.Default;
        options.Validate();
        Node ast = Parser.Parse(source, options.MaxDepth);
        BoundExpr root = new Binder(typeof(T), options).Bind(ast);
        if (root.Kind != ValueKind.Bool)
        {
            throw new RuleException(
                $"a rule must evaluate to a boolean but this expression is {Kinds.Describe(root)}",
                ast.Span.Start,
                Math.Max(1, ast.Span.Length));
        }

        if (root.MaybeNull)
        {
            root = new BoundTruthy(ast, root);
        }

        var compiler = new ExpressionCompiler(typeof(T), options);
        Expression<Func<T, bool>> tree = Expression.Lambda<Func<T, bool>>(compiler.Compile(root), compiler.Parameter);
        return new CompiledRule<T>(source, ast, root, tree, options);
    }

    /// <summary>
    /// Compiles an arbitrary typed expression (arithmetic, strings, dates...) to a delegate returning
    /// <typeparamref name="TResult"/>. TResult must be bool, long, decimal, double, string, DateOnly, DateTime or
    /// DateTimeOffset, or the Nullable form of a value type; a possibly-null expression needs a nullable TResult.
    /// </summary>
    public static CompiledExpression<T, TResult> Compile<T, TResult>(string source, RuleOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= RuleOptions.Default;
        options.Validate();
        Node ast = Parser.Parse(source, options.MaxDepth);
        BoundExpr root = new Binder(typeof(T), options).Bind(ast);
        Type target = typeof(TResult);
        Type targetBase = Nullable.GetUnderlyingType(target) ?? target;
        if (Kinds.ClrOf(root.Kind, root.EnumType) != targetBase)
        {
            throw new RuleException(
                $"the expression has type {Kinds.Describe(root)} and cannot be returned as {target.Name}",
                ast.Span.Start,
                Math.Max(1, ast.Span.Length));
        }

        if (target == typeof(bool) && root.MaybeNull)
        {
            root = new BoundTruthy(ast, root);
        }
        else if (root.MaybeNull && target.IsValueType && Nullable.GetUnderlyingType(target) == null)
        {
            throw new RuleException(
                $"the expression may be null, so it cannot be returned as {target.Name}; use {target.Name}? instead",
                ast.Span.Start,
                Math.Max(1, ast.Span.Length));
        }

        var compiler = new ExpressionCompiler(typeof(T), options);
        Expression body = ExpressionCompiler.ConvertValue(compiler.Compile(root), target);
        Expression<Func<T, TResult>> tree = Expression.Lambda<Func<T, TResult>>(body, compiler.Parameter);
        return new CompiledExpression<T, TResult>(source, ast, root, tree, options);
    }
}

/// <summary>A compiled boolean rule: expression tree, delegate, interpreter and explain mode.</summary>
public sealed class CompiledRule<T>
{
    private readonly BoundExpr _bound;
    private readonly Interpreter _interpreter;

    internal CompiledRule(string source, Node ast, BoundExpr bound, Expression<Func<T, bool>> tree, RuleOptions options)
    {
        Source = source;
        Ast = ast;
        Tree = tree;
        Predicate = tree.Compile();
        _bound = bound;
        _interpreter = new Interpreter(options);
    }

    public string Source { get; }

    public Node Ast { get; }

    /// <summary>The expression tree; usable as an <see cref="IQueryable{T}"/> predicate for the subset EF Core can translate.</summary>
    public Expression<Func<T, bool>> Tree { get; }

    public Func<T, bool> Predicate { get; }

    /// <summary>Evaluates with the compiled expression tree.</summary>
    public bool Evaluate(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Predicate(item);
    }

    /// <summary>Evaluates with the independent tree-walking interpreter.</summary>
    public bool Interpret(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return (bool)_interpreter.Eval(_bound, item)!;
    }

    /// <summary>Which sub-conditions were true / false / skipped (short-circuited) for this input.</summary>
    public ExplainNode Explain(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _interpreter.Explain(_bound, item);
    }
}

/// <summary>A compiled typed expression returning <typeparamref name="TResult"/>.</summary>
public sealed class CompiledExpression<T, TResult>
{
    private readonly BoundExpr _bound;
    private readonly Interpreter _interpreter;

    internal CompiledExpression(string source, Node ast, BoundExpr bound, Expression<Func<T, TResult>> tree, RuleOptions options)
    {
        Source = source;
        Ast = ast;
        Tree = tree;
        Func = tree.Compile();
        _bound = bound;
        _interpreter = new Interpreter(options);
    }

    public string Source { get; }

    public Node Ast { get; }

    public Expression<Func<T, TResult>> Tree { get; }

    public Func<T, TResult> Func { get; }

    public TResult Evaluate(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return Func(item);
    }

    /// <summary>Interpreter result, boxed: long, decimal, double, string, bool, date types, an enum, or null.</summary>
    public object? Interpret(T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _interpreter.Eval(_bound, item);
    }
}

/// <summary>
/// Helpers called from generated expression trees. Public only because compiled trees must be able to call them;
/// not part of the supported API.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class RuleRuntime
{
    public static bool RegexIsMatch(Regex regex, string input, int position)
    {
        try
        {
            return regex.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            throw new RuleException($"regular expression timed out after {regex.MatchTimeout.TotalMilliseconds:0} ms", position);
        }
    }

    public static long DaysSince(DateOnly value, DateTimeOffset now) =>
        DateOnly.FromDateTime(now.UtcDateTime).DayNumber - value.DayNumber;

    public static long DaysSince(DateTime value, DateTimeOffset now) => (now.UtcDateTime - value).Days;

    public static long DaysSince(DateTimeOffset value, DateTimeOffset now) => (now - value).Days;
}
