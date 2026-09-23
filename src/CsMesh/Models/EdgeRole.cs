namespace CsMesh.Models;

/// <summary>
/// How a member-access Call edge uses its target, as a set rather than a single value: a
/// property read on one line and written on another must stay the one edge the graph dedupes
/// on (from, to, kind), carrying both Read and Write.
///
/// Before this existed a property read and a property write were recorded as the same edge
/// with no way to tell them apart, so "where is this property written, not read" could only
/// be answered by reading every call site by hand. Subscribe is deliberately its own bit: an
/// event subscription is not a value write and must not answer a --writes query.
/// </summary>
[Flags]
public enum EdgeRole
{
    Read = 1,
    Write = 2,
    Subscribe = 4
}
