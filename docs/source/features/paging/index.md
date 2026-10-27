# Result paging

## Automatic paging 

You can iterate indefinitely over the `RowSet`, having the rows fetched block by block until the rows available on the
client side are exhausted.

```csharp
var ps = session.Prepare("SELECT * from tbl1 WHERE key = ?");
// Set the page size at statement level.
var statement = ps.Bind(key).SetPageSize(1000);
var rs = session.Execute(statement);
foreach (var row in rs)
{
   // The enumerator will yield all the rows from Cassandra.
   // Retrieving them in the back in blocks of 1000.
}
```

### Asynchronous automatic paging

Iterating the `RowSet` returned by `Execute()` blocks the calling thread while each following page is retrieved.
`ExecuteAsync()` returns an `AsyncRowSet` instead, which implements `IAsyncEnumerable<Row>`: iterate it with
`await foreach` to retrieve the following pages without blocking.

```csharp
var ps = await session.PrepareAsync("SELECT * from tbl1 WHERE key = ?");
var statement = ps.Bind(key).SetPageSize(1000);
var rs = await session.ExecuteAsync(statement);
await foreach (var row in rs)
{
   // The async enumerator yields all the rows from the database,
   // retrieving them in the background in blocks of 1000.
}
```

The asynchronous LINQ operators of .NET 10 (`System.Linq.AsyncEnumerable`) apply to an `AsyncRowSet` directly, and
`WithCancellation()` passes a cancellation token to the enumeration:

```csharp
List<Row> rows = await rs.ToListAsync(cancellationToken);

await foreach (var row in rs.WithCancellation(cancellationToken))
{
   // ...
}
```

An `AsyncRowSet` also exposes the `Info`, `Columns` and `PagingState` of the result, and `FetchMoreResultsAsync()`
retrieves the following page before the current one has been consumed.

Cancellation stops waiting for the following page. A page request that was already sent is not cancelled: once it
completes, its rows are added to the `AsyncRowSet`, and the next enumerator that dequeues rows yields them. That can be
an enumerator running concurrently rather than the next enumeration.

The query abort timeout (`Builder.WithQueryTimeout()`) applies only to synchronous operations, such as `Execute()` and
iterating a `RowSet`, which stop waiting once it expires. `ExecuteAsync()` does not apply it to the first page, and
iterating an `AsyncRowSet` does not apply it to the following ones: each page request is bounded by the socket read
timeout (`SocketOptions.SetReadTimeoutMillis()`) only, so pass a cancellation token to stop waiting earlier.

Concurrent enumerations of the same `RowSet` or `AsyncRowSet` are thread-safe: each row is yielded once, and concurrent
enumerators share the request for the following page instead of sending it again. A page whose request failed is
requested again by the next enumeration.

If the session is disposed before all pages are retrieved, retrieving the following page throws an
`ObjectDisposedException`.

## Manual paging 

If you want to retrieve the next page of results only when you ask for it (for example, in a webpager), use the
`PagingState` property in the `RowSet` to execute the following statement.

```csharp
var ps = session.Prepare("SELECT * from tbl1 WHERE key = ?");
// Disable automatic paging.
var statement = ps
   .Bind(key)
   .SetAutoPage(false)
   .SetPageSize(pageSize);
var rs = session.Execute(statement);
// Store the paging state
var pagingState = rs.PagingState;

// Later in time ...
// Retrieve the following page of results.
var statement2 = ps
   .Bind(key)
   .SetAutoPage(false)
   .SetPagingState(pagingState)
var rs2 = Session.Execute(statement2);
```

Note: The `PagingState` property is not encrypted and can be used to inject values to retrieve other partitions, so be
careful not to expose it to the end user.

## Automatic paging in LINQ and Mapper components 

Both LINQ and Mapper queries support automatic paging: as you iterate through the mapped results, it fetches the
following pages. If you want to manually page, you can use Linq's `ExecutePaged()` method, Mapper's `FetchPage()`, or
their async counterparts.

A LINQ paging example:

```csharp
// Providing page size.
IPage<User> adminUsers = users
   .Where(u => u.Group == "admin")
   .SetPageSize(pageSize)
   .ExecutePaged();


// Providing paging state (following pages).
IPage<User> adminUsers = users
   .Where(u => u.Group == "admin")
   .SetPageSize(pageSize)
   .SetPagingState(pagingState)
   .ExecutePaged();
```

A Mapper paging example:

```csharp
IPage<User> users = mapper.FetchPage<User>(pageSize, pagingState, query, parameters);

// Or using query options

IPage<User> authors = mapper.FetchPage<User>(
      Cql.New(query, parameters).WithOptions(opt =>
            opt.SetPageSize(pageSize).SetPagingState(state)));
```