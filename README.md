# dotnet-rules-engine

A small business-rules expression language for .NET 10 that compiles to `System.Linq.Expressions`. It is the kind of
thing a customer-configurable filter or eligibility rule needs: a rule is plain text such as

```
age >= 18 and country in ['US','CA'] and not (status == "closed") and name startswith "A" or vip
```

and becomes a typed `Expression<Func<T,bool>>` (and then a delegate) after being lexed, parsed, type-checked against a
CLR type and compiled. The same checked tree can also be evaluated by an independent tree-walking interpreter, printed
back to text, explained condition by condition, and (for the subset that translates) handed to EF Core as a query
predicate.

This is a portfolio / showcase project, not a product. CI ran green on the first run on GitHub Actions (ubuntu-latest, 2026-10-20).

## What is in the box

| Piece | File | Notes |
|---|---|---|
| Lexer | `src/RulesEngine/Lexer.cs` | hand-written; positions, string escapes, decimals, dotted identifiers, case-insensitive keywords |
| Parser | `src/RulesEngine/Parser.cs` | Pratt / precedence climbing, spans on every node, depth-limited |
| AST + printer | `Ast.cs`, `Printer.cs` | minimal-parentheses pretty printer; `parse(print(ast))` equals `ast` |
| Binder | `src/RulesEngine/Binder.cs` | type checker against `T`, errors with position and "did you mean" (edit distance) |
| Compiler | `ExpressionCompiler.cs`, `RuleCompiler.cs` | bound tree -> `Expression<Func<T,bool>>` or `Expression<Func<T,TResult>>` |
| Interpreter | `Interpreter.cs` | second, independent implementation of the semantics; also drives explain mode |
| Rule sets | `RuleSet.cs` | named rules with priority, first-match / all-match, explain, rule-file reader |
| Demo | `demo/RulesEngine.Demo` | synthetic customers + rule file, explain output, REPL behind `--repl` |
| Tests | `tests/RulesEngine.Tests` | xunit, 604 test cases (see below) |

The library has no dependencies. EF Core (SQLite provider) is referenced by the **test project only**.

## The language

### Grammar (EBNF)

```ebnf
rule           = expr ;
expr           = or_expr ;
or_expr        = and_expr   { "or"  and_expr } ;
and_expr       = not_expr   { "and" not_expr } ;
not_expr       = "not" not_expr | comparison ;
comparison     = additive [ cmp_tail ] ;                 (* at most ONE: comparisons do not chain *)
cmp_tail       = ( "==" | "!=" | "<" | "<=" | ">" | ">=" ) additive
               | ( "startswith" | "endswith" | "contains" | "matches" ) additive
               | [ "not" ] "in" "[" expr { "," expr } "]"
               | [ "not" ] "between" additive "and" additive
               | "is" [ "not" ] "null" ;
additive       = multiplicative { ( "+" | "-" ) multiplicative } ;
multiplicative = unary { ( "*" | "/" | "%" ) unary } ;
unary          = "-" unary | primary ;
primary        = number | string | "true" | "false" | path | call | "(" expr ")" ;
call           = name "(" [ expr { "," expr } ] ")" ;      (* name must be on the whitelist *)
path           = ident { "." ident } ;                     (* address.city, no spaces around the dots *)
number         = digits [ "." digits ] ;                   (* no sign, no exponent; "1.5" is always 1.5 *)
string         = '"' ... '"' | "'" ... "'" ;               (* escapes: \\ \" \' \n \r \t \uXXXX *)
```

Keywords are case-insensitive (`AND`, `Not`). Identifiers and function names are case-insensitive too, and
identifiers are ASCII (`[A-Za-z_][A-Za-z0-9_]*`). The parser accepts `not` and unary `-` anywhere an operand can start
(`1 + not x` parses and is then rejected by the type checker).

### Precedence (loosest to tightest)

| Level | Operators | Associativity |
|---:|---|---|
| 10 | `or` | left |
| 20 | `and` | left |
| 30 | `not` (prefix) | right |
| 40 | `==` `!=` `<` `<=` `>` `>=`, `in`, `not in`, `between`, `not between`, `is [not] null`, `startswith` `endswith` `contains` `matches` | **none**: `a < b < c` is a parse error |
| 50 | `+` `-` | left |
| 60 | `*` `/` `%` | left |
| 70 | unary `-` | right |

So `not a == b` is `not (a == b)`, `not a and b` is `(not a) and b`, and
`a and b or c` is `(a and b) or c`. The operands of `between` are parsed above level 40, so
`x between 1 and 5 and y` is `(x between 1 and 5) and y`. Parenthesised operands keep the span of their contents
(the parentheses are not part of a node's span). Rules nest at most 256 levels deep (configurable): parentheses,
prefix operators and the height of the tree all count, so a flat chain of more than about 255 `or` terms is rejected.

### Functions: a fixed whitelist, nothing else

| Function | Argument | Result | Null |
|---|---|---|---|
| `len(s)` | string | integer | null in, null out |
| `lower(s)`, `upper(s)` | string | string (invariant culture) | null in, null out |
| `abs(n)` | integer, decimal or double | same type | null in, null out |
| `now()` | none | datetimeoffset (adapts to date / datetime when compared with one) | never null; read from an injected `TimeProvider` |
| `daysSince(d)` | date, datetime or datetimeoffset | integer, whole days, truncated toward zero | null in, null out |

### Types

Every CLR type a rule can touch collapses to one of nine kinds:

| Kind | CLR types |
|---|---|
| boolean | `bool`, `bool?` |
| integer | `sbyte byte short ushort int uint long` and their nullable forms (all widen to `long`) |
| decimal | `decimal`, `decimal?` |
| double | `float double` and nullable forms |
| string | `string` (always treated as possibly null) |
| date / datetime / datetimeoffset | `DateOnly`, `DateTime`, `DateTimeOffset` and nullable forms |
| enum | any enum, nullable or not; written as a string literal naming a member, case-insensitive |

Anything else (`Guid`, collections, `char`, `ulong`, ...) is rejected with an error that names the property and its type.
Nested objects can be navigated with dots (`address.city`) but cannot themselves be tested: test a leaf instead
(`address.city is null`).

**Promotion rules**

* integer widens to decimal or double.
* A **decimal literal** adapts to double (`score > 0.5` works when `score` is a `double`), but decimal **values** and
  double **values** never mix: `price + score` is an error. This is deliberate; there is no silent precision loss.
* `integer * 2.5` is decimal (the literal is a decimal), so a double result needs a double operand.
* A string literal compared with an enum becomes that enum member (unknown names get a suggestion);
  a string literal compared with a date kind is read as ISO text (`"2025-06-05"`, `"2025-06-05T10:00:00Z"`,
  offsets allowed; no offset means UTC) and an invalid date is an error at compile time, not at run time.
* `datetime` and `datetimeoffset` do not mix with each other or with `date`; `now()` adapts to whichever it is compared with.
  `DateTime` values are compared as written (their `Kind` is ignored; treat them as UTC).
* Strings compare **ordinally** (case-sensitive); use `lower()` for case-insensitive tests. `startswith`, `endswith`
  and `contains` are ordinal. Booleans and enums support only `==`, `!=` and `in`.
* Arithmetic is unchecked (integer overflow wraps). Division or modulo by zero (or by a null) yields **null**, for all
  three numeric kinds, instead of throwing or producing infinity.

### Null semantics (two-valued logic)

Rules never see SQL-style three-valued logic. A value can be null (nullable property, string, or any path through a
null nested object), and:

| Construct | Null operand gives |
|---|---|
| `== != < <= > >=` | **false** (including `!=`, and `null == null` is false) |
| `in`, `not in`, `between`, `not between` | **false** (`not in` is `!=` against every item, so a null item also gives false) |
| `startswith endswith contains matches` | **false** |
| `+ - * / %`, unary `-`, `len lower upper abs daysSince`, string `+` | **null** (propagates) |
| a null member path (`child.value` when `child` is null) | **null** |
| `is null` / `is not null` | the only way to test for null |
| `not x`, `and`, `or`, a bare nullable boolean | null boolean is false; `not` is applied **after** that, so `not (b == 5)` is **true** when `b` is null |

The last row is the one to remember: `b != 5` and `not (b == 5)` differ when `b` is null. `and` / `or` short-circuit;
the right side is not evaluated when the left decides (tested with a regex that would otherwise time out).
The semantics are pinned by a 57-row table test and a 54-row table of non-null cases in `SemanticsTests`,
each evaluated by both implementations.

## Security model

A rule is untrusted text. What it can do is limited by construction, not by filtering:

* **Members**: the binder only ever finds **public, readable, non-indexer instance properties** of the root type and of
  nested class types. It never looks up methods. Private, internal, protected and static members, fields and indexers
  are "unknown property", and suggestions never offer a non-public name.
* **Functions**: exactly six (`len lower upper abs now daysSince`), looked up in a fixed table. Any other call
  (`exec(...)`, `name.ToUpper()`, `System.Environment.Exit(0)`, `typeof(x)`) is a `RuleException` carrying a position.
  There is no reflection access to arbitrary methods and no way to name a type.
* **Generated code**: a test walks the expression tree of a corpus covering every construct and asserts that the only
  methods it calls are `string.StartsWith/EndsWith/Contains/ToLowerInvariant/ToUpperInvariant/Concat/CompareOrdinal`,
  `Math.Abs`, `DateOnly.FromDateTime`, `TimeProvider.GetUtcNow`, two helpers in this library, and the
  operators of `decimal/DateOnly/DateTime/DateTimeOffset/string`; that no `Invoke` or `New` node appears; and that every
  member read is a public property.
* **Regular expressions**: `matches` takes a **string literal** pattern (so data cannot choose a pattern), validated at
  compile time, and runs with a timeout (default 250 ms). A catastrophic pattern such as `^(a+)+$` on `aaaa...a!` ends in
  a `RuleException`, in both implementations.
* **Hostile text**: lexer, parser and binder only ever throw `RuleException` (fuzz-tested), and nesting is bounded, so
  10,000 nested parentheses is a clean error rather than a stack overflow.

**What it does not protect against** (read this before exposing it to customers):

* It is not a sandbox. Property **getters on your model run**, with whatever side effects or exceptions they have.
  Compile rules against a purpose-built, read-only DTO, not your domain entities.
* Rules can read every public property of that model; there is no per-property authorisation.
* Legal rules can still be expensive: a long `in [...]` list, many string operations, or a regex that is slow but under
  the timeout (the timeout is per match, not per rule or per query). There is no limit on rule length and no CPU budget.
* An evaluation can throw a non-`RuleException` for a property getter that throws, and arithmetic that overflows
  `decimal`. `abs(long.MinValue)` and `long.MinValue / -1` are expected to throw `OverflowException` too; these are
  not covered by tests.
* Only the in-memory path was tested for hostile *input*. The EF path was tested for what translates, not for injection
  (EF parameterises constants; nothing here concatenates SQL).

## Usage

```csharp
using RulesEngine;

public sealed class Customer { public int Age { get; init; } public string Country { get; init; } = ""; public bool Vip { get; init; } }

var options = new RuleOptions { TimeProvider = TimeProvider.System };      // inject a fixed clock in tests
CompiledRule<Customer> rule = RuleCompiler.Compile<Customer>(
    "age >= 18 and country in ['US','CA'] or vip", options);

bool ok = rule.Evaluate(customer);               // compiled expression tree
bool same = rule.Interpret(customer);            // independent tree-walking interpreter
Expression<Func<Customer,bool>> tree = rule.Tree; // usable with IQueryable (see EF section)
Console.WriteLine(rule.Explain(customer).ToText());

// arithmetic / other result types
var price = RuleCompiler.Compile<Order, decimal?>("subtotal * 1.2 + shipping");

// rule sets: priority descending, ties keep declaration order
var set = RuleSet<Customer>.Create(RuleFile.Parse(File.ReadAllText("rules.txt")));
NamedRule<Customer>? first = set.FirstMatch(customer);
IReadOnlyList<NamedRule<Customer>> all = set.AllMatches(customer);

// errors
try { RuleCompiler.Compile<Customer>("contry == \"US\""); }
catch (RuleException ex) { Console.WriteLine(ex.Render("contry == \"US\"")); }  // message, caret, suggestion
```

A rule file has one rule per line, `name [priority N]: expression`; `#` starts a comment line.

## Demo (real output)

`dotnet run --project demo/RulesEngine.Demo -- --customers samples/customers.csv --rules samples/rules.txt`
(clock fixed at 2026-10-20T12:00:00Z so the output is reproducible; `--now system` uses the real clock;
all customers are invented).

```text
rules-engine demo
clock: 2026-10-20T12:00:00Z (fixed; use --now system for the real clock)
loaded 12 customers
loaded 9 rules (evaluation order: priority descending)
   30  vip_review       vip and balance between 1000 and 20000
   20  eligible_us_ca   age >= 18 and country in ['US','CA'] and not (status == "closed") and name startswith "A" or vip
   15  dormant          lastLogin is null or daysSince(lastLogin) > 90
   12  at_risk          status == "Suspended" or balance < 0
   10  no_email         email is null
    8  good_score       score * 100 >= 75.0
    5  local_city       address.city matches "^(Austin|Toronto)$"
    3  long_tenure      daysSince(signup) > 365 * 5
    1  senior           age >= 60

all-match: every rule that matches each customer
  C001 Alice Smith    vip_review, eligible_us_ca, good_score, local_city, long_tenure
  C002 Aaron Blake    (none)
  C003 Chloe Martin   good_score, local_city, long_tenure
  C004 Bob Okafor     dormant, at_risk, no_email
  C005 Diego Alvarez  vip_review, eligible_us_ca, good_score, long_tenure
  C006 Anna Petrov    dormant, long_tenure
  C007 Emma Larsen    dormant
  C008 Frank Mueller  long_tenure, senior
  C009 Grace Kim      at_risk
  C010 Hiro Tanaka    vip_review, eligible_us_ca, no_email, good_score, long_tenure
  C011 Alicia Gomez   eligible_us_ca, dormant
  C012 Ivan Novak     dormant, long_tenure, senior

first-match: highest-priority matching rule per customer
  C001 Alice Smith    vip_review
  C002 Aaron Blake    (none)
  C003 Chloe Martin   good_score
  C004 Bob Okafor     dormant
  C005 Diego Alvarez  vip_review
  C006 Anna Petrov    dormant
  C007 Emma Larsen    dormant
  C008 Frank Mueller  long_tenure
  C009 Grace Kim      at_risk
  C010 Hiro Tanaka    vip_review
  C011 Alicia Gomez   eligible_us_ca
  C012 Ivan Novak     dormant

hits per rule: vip_review=3, eligible_us_ca=4, dormant=5, at_risk=2, no_email=2, good_score=4, local_city=2, long_tenure=7, senior=2

explain C002 Aaron Blake / rule eligible_us_ca: no match
  [FALSE] OR
    [FALSE] AND
      [FALSE] AND
        [FALSE] AND
          [FALSE] age >= 18
          [SKIP ] country in ["US", "CA"]
        [SKIP ] NOT
      [SKIP ] name startswith "A"
    [FALSE] vip

explain C007 Emma Larsen / rule dormant: MATCH
  [TRUE ] OR
    [TRUE ] lastLogin is null
    [SKIP ] daysSince(lastLogin) > 90

bad rules are rejected with a position and, for unknown names, a suggestion:
  unknown property 'contry' on type Customer (at position 0). Did you mean 'Country'?
    contry == "US"
    ^^^^^^
  operator '>=' cannot be applied to integer and string (at position 0)
    age >= "18"
    ^^^^^^^^^^^
  'Clsoed' is not a member of enum CustomerStatus (at position 10). Did you mean 'Closed'?
    status == "Clsoed"
              ^^^^^^^^
  unknown property 'cty' on type Address (at position 8). Did you mean 'address.City'?
    address.cty == "Austin"
            ^^^
  method calls are not supported ('name.ToUpper(...)'); only whitelisted functions such as len(...) can be called (at position 0)
    name.ToUpper() == "A"
    ^^^^^^^^^^^^
  expected an expression but found the end of the rule (at position 13)
    age >= 18 and
                 ^
```

`[SKIP ]` means short-circuited: that condition was never evaluated for this input.

### REPL

`--repl` reads rules from standard input (a prompt is printed only when stdin is a terminal). `:help`, `:explain <id> <rule>`,
`:print <rule>`, `:schema`, `:customers`, `:quit`. A real piped session:

```text
> age >= 18 and country in ["US","CA"] and not (status == "closed") and name startswith "A" or vip
  4 of 12 match: C001, C005, C010, C011
> balance between 1000 and 20000 and address.city is not null
  4 of 12 match: C001, C005, C008, C010
> contry == "US"
unknown property 'contry' on type Customer (at position 0). Did you mean 'Country'?
  contry == "US"
  ^^^^^^
> :explain C011 age >= 30 and (vip or lastLogin is null)
  C011 Alicia Gomez:
    [FALSE] AND
      [TRUE ] age >= 30
      [FALSE] OR
        [FALSE] vip
        [FALSE] lastLogin is null
> :print daysSince(signup)>365*5 AND not(vip)
  daysSince(signup) > 365 * 5 and not vip
```

## How it is tested

604 xunit test cases (a theory row counts as one), all passing locally on Windows with the .NET 10.0.402 SDK:

| Area | Cases | What |
|---|---:|---|
| `LexerTests` | 72 | known-answer tokens and spans, every operator and keyword, escapes, decimals, 20 lexical errors with exact positions |
| `ParserTests` | 76 | 37-row precedence/associativity table, chained-comparison rejection, spans, 17 syntax errors with positions |
| `PrinterTests` | 69 | known-answer minimal-parenthesis output, 36-rule round-trip corpus, string-escape round trips, AST equality |
| `BinderTests` | 70 | unknown names with positions and suggestions, 33 type-error messages, inaccessible members, ambiguity |
| `SemanticsTests` | 132 | null-semantics table, value table, decimal-vs-double promotion, division by zero, typed results, short-circuit |
| `SecurityTests` | 54 | no arbitrary methods, no private/static/field/indexer access, expression-tree method whitelist walk, regex timeout |
| `CultureTests` | 11 | `de-DE`, `fr-FR`, `tr-TR`, `sv-SE`, `ar-SA` current cultures: `1.5` still parses as 1.5, ordinal/invariant behaviour |
| `DepthAndFuzzTests` | 27 | 10,000-deep parentheses and other deep shapes, seeded fuzzing |
| `OracleTests` | 43 | the three oracles below |
| `RuleSetTests`, `ExplainTests` | 16 + 11 | priority order, first/all match, rule files, explain trees |
| `DemoTests`, `CompilerApiTests` | 9 + 12 | CSV loader, demo output, REPL script, options, error rendering |
| `EfCoreTests` | 2 | the translation table below (42 rules) |

### The three oracles

A compiler needs an independent definition of "correct", so the same rules are checked three ways:

1. **Compiled == interpreted**: a seeded, type-correct random rule generator (`RuleGenerator`, every operator, function,
   null case, enum, date, decimal and double combination) produced 5 seeds x 120 rules, each run against 60 random
   objects: **36,000 evaluations**, compiled expression tree vs interpreter (and the explain tree's verdict). About 5 in 8
   rules produce both true and false on the sample, so the oracle is not comparing constants. Another 450 random
   arithmetic expressions (integer, decimal, double) are compared as typed results.
2. **Both == a hand-written C# lambda**: 27 fixed rules x 5,000 seeded random objects. Compiled, interpreted and the
   lambda must agree on every object (135,000 object checks per implementation), and each rule must be neither always
   true nor always false on the sample.
3. **Print/parse round-trip**: 5 seeds x 200 generated rules: `parse(print(parse(text)))` is structurally equal to `parse(text)`,
   printing is idempotent, and the reprinted rule evaluates identically on 25 objects.

The interpreter reuses the binder (names, types, literal conversion) but not the compiler, so a binder bug would not be
caught by oracle 1; oracle 2 (independent C#) and the table tests are what cover it.

### Fuzzing

Seeded, deterministic: 24,000 random character strings, 24,000 random token sequences, 16,000 mutated valid rules (and 1,500
mutated generated rules, plus every prefix of every seed rule) go through lex/parse and, for the mutated ones, bind, compile
and evaluate on two objects. The test fails on anything except an AST, a compiled rule, or `RuleException`. No NullReference,
IndexOutOfRange or StackOverflow was found in the final code. The fuzz and oracle tests did catch one real bug during development:
date-string literals compared with a `DateTime` property were boxed as `DateTimeOffset` (a ternary between the two types
silently picks `DateTimeOffset`), so the expression tree failed to build with "Argument types do not match". It is fixed
and covered by the date tests.

## EF Core (SQLite) translation, reported honestly

The test project maps a `Person` entity to an in-memory SQLite database (EF Core 10.0.0, `Microsoft.EntityFrameworkCore.Sqlite`),
seeds 80 rows, compiles each rule to `Expression<Func<Person,bool>>` and runs `db.People.Where(rule.Tree)`. "Translates" means
the SQL ran **and** returned exactly the same ids as the in-memory delegate on that data.
These results are for this provider and version only; other providers (SQL Server, PostgreSQL) were not tried.

| Translates (same rows) | Throws (`InvalidOperationException` / `NotSupportedException`) |
|---|---|
| comparisons, `and` / `or` / `not`, `bool == true` | `startswith` / `endswith` in the default mode (uses the `StringComparison` overload) |
| `in`, `not in` with literal lists, `between` | division or modulo by a **non-constant** (compiled with a variable block) |
| `contains`, `len(x)`, `abs(x)` | `lower(x)`, `upper(x)` (uses `ToLowerInvariant` / `ToUpperInvariant`; not isolated further) |
| `is null` / `is not null`, nullable comparisons, nested `home.city` (EF complex property) | `matches` (calls a helper in this library) |
| `+ - *`, `/` and `%` by a non-zero constant (integer, decimal and double) | `daysSince(x)` (calls a helper in this library) |
| string `==` a literal, string `+` inside a comparison | comparing a `DateTimeOffset` column (provider limitation) |
| enums by name (`tier == "Red"`, `tier in [...]`) | string ordering (`name > "M"`, compiled to `CompareOrdinal`) |
| decimal comparison and multiplication, `DateOnly` vs a date literal | |
| `now()` (EF evaluates the clock call on the client and sends a parameter) | |
| `startswith` / `endswith` with `RuleOptions.QueryFriendly = true` | |

`QueryFriendly` emits the parameterless `StartsWith(string)` / `EndsWith(string)`, which EF translates but which are
**culture-sensitive** when evaluated in memory, so leave it off for in-memory use. A shared sub-expression that is not
trivially repeatable is stored in a variable (`Block`); EF cannot translate blocks, which is why division by a non-constant
fails. Cheap expressions (property reads, `+ - *`, conversions, null checks, up to 24 nodes) are repeated instead, which keeps
most rules block-free. Because rules use two-valued null logic and EF compensates for SQL's three-valued logic, even
`not (level > 3)` returned the same rows as the delegate on the seeded data; that is a result for this data set, not a proof.

## Mutation testing

### Tool: Stryker.NET tried first, then a hand-written script

* **Stryker.NET 5.0.0** installs and runs on .NET 10 (`dotnet tool install dotnet-stryker --tool-path ...`). Scoped to the lexer,
  parser, printer and edit-distance files it generated 518 testable mutants (plus 191 compile-error mutants and 2 uncovered ones),
  but on this shared machine (other builds and other Stryker runs were using the CPU) it had produced **no result after about 25 minutes**
  with no visible progress, so I stopped it (only the process tree I had started) and did **not** get a Stryker score. I make no
  Stryker claim beyond "it runs".
* **`scripts/mutation_check.py`** (what the numbers below come from): 42 hand-picked single-text mutations of `src/RulesEngine`, each
  required to match exactly once, restored in a `finally` block, whole suite run per mutant (`dotnet test --no-restore`, 240 s timeout),
  and `src/` compared byte-for-byte with a backup taken before the run (identical after every run). Mutants are targeted at
  precedence, associativity, depth limits, null/decimal/enum rules, `not in` / `between`, division by zero, explain short-circuiting,
  priorities, regex timeout and the clock. This is a hand-picked sample, **not** exhaustive mutation coverage.

### Result

First run: 42 mutants, 4 invalid (the mutation made unreachable code, which `TreatWarningsAsErrors` turned into a compile error), 38 valid,
**36 killed, 2 survived**. After passing `-p:TreatWarningsAsErrors=false` for mutants and adding two tests for the survivors, the four
invalid ones and both survivors were re-run. Final: **42 of 42 mutants killed** (40 by a failing assertion, 1 by a test-host crash,
1 by timeout).

| Kill kind | Mutants |
|---|---|
| failing assertion | 40 |
| **test-host crash** (stack overflow) | M10: recursion limit effectively removed; 10,000 nested parentheses overflowed the stack and killed the test host |
| **timeout** (240 s, tree killed by the script) | M26: regex timeout removed; the catastrophic-pattern test never returns, so only the timeout ends it |

Survivors from the first run and what was done:

| Mutant | Verdict | Action |
|---|---|---|
| M04 lexer: a bare carriage return no longer ends a string literal | **test gap** (only `
` was tested) | added the `"ab"` row to `LexerTests`; re-run: killed |
| M19 edit distance: ties between equally close candidates no longer broken by ordinal order | **test gap** (suggestions were deterministic by design but untested) | added `Suggest("nam", ["Name","Nama"])` in both input orders; re-run: killed |

I found no equivalent mutants in this sample. Not examined: any mutation outside these 42 (so there may be survivors I did not generate).
Mutants M23, M26, M29 and M40 first reported "invalid" only because of warnings-as-errors; with that flag off they compile and were killed.
Run it yourself: `python scripts/mutation_check.py --check` (patterns), `python scripts/mutation_check.py` (all), `MUTANT_TIMEOUT=240` by default 300.

## Known limits

* Not a general scripting language: no variables, no user-defined functions, no loops, no list values (lists exist only after `in`),
  no member calls, no indexers, no collections, no `ulong`/`Guid`/`char`.
* `x is null` works on leaf properties only, not on a nested object itself.
* Strings are compared ordinally; there is no case-insensitive operator (use `lower()`).
* A rule nests at most 256 levels (configurable via `RuleOptions.MaxDepth`); a flat chain of more than ~255 `and`/`or` terms is rejected.
* Compiled rules are not cached for you; compile once and reuse the delegate. Performance was **not benchmarked**.
* The interpreter shares the binder with the compiler (see oracles).
* Tested on Windows only with .NET 10.0.402; the CI workflow ran green once on Ubuntu, but I did not test by hand on Linux or macOS. Only the SQLite EF provider was tried.
* The test project pulls `Microsoft.EntityFrameworkCore.Sqlite` 10.0.0, whose native `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 triggers a
  NuGet audit warning (NU1903); it is kept as a warning (test project only, in-memory database) rather than hidden.

## Build and run

```bash
dotnet restore && dotnet build -warnaserror && dotnet test
dotnet run --project demo/RulesEngine.Demo -- --customers samples/customers.csv --rules samples/rules.txt
dotnet run --project demo/RulesEngine.Demo -- --repl
```

`nuget.config` clears all package sources and adds only nuget.org so restore works on machines with no user-level feed.

CI: `.github/workflows/ci.yml` (Ubuntu, `actions/checkout@v7`, `actions/setup-dotnet@v6`, restore, build with `-warnaserror`, test,
run the demo). CI ran green on the first run on GitHub Actions (ubuntu-latest, 2026-10-20).

## Clone and license

```bash
git clone https://github.com/Supp3r-Man/dotnet-rules-engine.git
```

MIT, see `LICENSE`.
