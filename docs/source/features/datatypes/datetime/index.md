# Date and time representation

## Timestamp

A single moment in time with millisecond precision, is represented as `DateTimeOffset`.

## Date

Introduced in Cassandra 2.2, a date portion without a time-zone, is represented as a [LocalDate][localdate-api].

A `DateOnly` can be used instead:

- **Parameters:** a `DateOnly` value can be bound to a `date` parameter, in a statement, in a mapper POCO or UDT, or in a LINQ expression. A POCO property of type `DateOnly` creates a `date` column.
- **Reading:** a `date` column is read as a `LocalDate`. `Row.GetValue<DateOnly>()`, collections of `DateOnly` and `DateOnly` properties of mapper POCOs and UDTs convert it.
- **Range:** a CQL `date` ranges from year -5877641 to year 5881580, and a `DateOnly` from 0001-01-01 to 9999-12-31. Reading a date outside that range as a `DateOnly` throws an `ArgumentOutOfRangeException`. When the driver converts the value through reflection, the `ArgumentOutOfRangeException` is wrapped in a `TargetInvocationException`: for a `DateOnly?` property of a mapper POCO, for the `DateOnly?` elements of a collection property, and for a field of a UDT. For a `DateOnly?` field of a UDT, or the `DateOnly?` elements of a UDT field, it's wrapped twice. A UDT value outside the range fails the request that reads it. Use `LocalDate` for those dates.

`LocalDate.ToDateOnly()` and `LocalDate.FromDateOnly(DateOnly)` convert between the two types.

## Time

Introduced in Cassandra 2.2, a time portion without a time-zone, is represented as a [LocalTime][localtime-api].

A `TimeOnly` can be used instead, in the same places as a `DateOnly` for a `date`. A `time` has a precision of one nanosecond, and a `TimeOnly` of 100 nanoseconds: reading a `time` as a `TimeOnly` truncates the nanoseconds below that. Two values that differ only below 100 nanoseconds then become equal: a `set<time>` read as a set of `TimeOnly` keeps one of them, and a `map<time, …>` read with `TimeOnly` keys throws an `ArgumentException`.

`LocalTime.ToTimeOnly()` and `LocalTime.FromTimeOnly(TimeOnly)` convert between the two types.

[localdate-api]: https://docs.datastax.com/en/drivers/csharp/latest/api/Cassandra.LocalDate.html
[localtime-api]: https://docs.datastax.com/en/drivers/csharp/latest/api/Cassandra.LocalTime.html
