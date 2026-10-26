namespace Leaf
{
    // Why a translation starts, stated explicitly instead of guessed from the source kind:
    // a root replaces the return relation, an inherit keeps the current anchor (retry,
    // regenerate), and an internal child stays anchored to the root it was born from.
    internal enum SessionIntent
    {
        Root,
        Inherit,
        InternalChild
    }
}
