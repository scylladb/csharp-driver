# Parameterized queries

You can bind the values of parameters in a `BoundStatement` or `SimpleStatement` either by position or by using
named markers.

Bind anonymous `?` markers by position, and bind by name only when the query declares explicit
`:name` markers. Names that the server generates for `?`, such as `IN(column)` or `in(column)`, can
change across server versions and must not be used as an application contract. See
[prepared statements](../components/core/statements/prepared/index) for `IN` examples, automatic
repreparation, and mixed-version rollout limits.

## Positional parameters example

```csharp
var statement = session.Prepare("SELECT * FROM table where a = ? and b = ?");
// Bind parameter by marker position 
session.Execute(statement.Bind("aValue", "bValue"));
```

## Named parameters example

You can declare the named markers in your queries and use as parameter names when binding.

```csharp
var statement = session.Prepare("SELECT * FROM table where a = :a and b = :b"); 
// Bind by name using anonymous types 
session.Execute(statement.Bind( new { a = "aValue", b = "bValue" }));
```
