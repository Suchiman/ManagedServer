namespace ManagedServer;

public static class Extensions
{
    public static IEnumerable<TResult> FullOuterJoin<TLeft, TRight, TKey, TResult>(
        this IEnumerable<TLeft> a,
        IEnumerable<TRight> b,
        Func<TLeft, TKey> selectKeyA,
        Func<TRight, TKey> selectKeyB,
        Func<TLeft, TRight, TKey, TResult> projection,
        TLeft? defaultA = default(TLeft),
        TRight? defaultB = default(TRight),
        IEqualityComparer<TKey>? cmp = null)
    {
        cmp ??= EqualityComparer<TKey>.Default;
        var alookup = a.ToLookup(selectKeyA, cmp);
        var blookup = b.ToLookup(selectKeyB, cmp);

        var keys = new HashSet<TKey>(alookup.Select(p => p.Key), cmp);
        keys.UnionWith(blookup.Select(p => p.Key));

        return from key in keys
               from xa in alookup[key].DefaultIfEmpty(defaultA)
               from xb in blookup[key].DefaultIfEmpty(defaultB)
               select projection(xa, xb, key);
    }

    public static IEnumerable<(TLeft Left, TRight Right, TKey Key)> FullOuterJoin<TLeft, TRight, TKey>(
        this IEnumerable<TLeft> a,
        IEnumerable<TRight> b,
        Func<TLeft, TKey> selectKeyA,
        Func<TRight, TKey> selectKeyB,
        TLeft? defaultA = default(TLeft),
        TRight? defaultB = default(TRight),
        IEqualityComparer<TKey>? cmp = null)
    {
        cmp ??= EqualityComparer<TKey>.Default;
        var alookup = a.ToLookup(selectKeyA, cmp);
        var blookup = b.ToLookup(selectKeyB, cmp);

        var keys = new HashSet<TKey>(alookup.Select(p => p.Key), cmp);
        keys.UnionWith(blookup.Select(p => p.Key));

        return from key in keys
               from xa in alookup[key].DefaultIfEmpty(defaultA)
               from xb in blookup[key].DefaultIfEmpty(defaultB)
               select (xa, xb, key);
    }
}
