namespace SmartX.Shared.Telemetry;

/// <summary>
/// A self-balancing binary search tree (AVL). Every node keeps the heights of its two
/// subtrees within one of each other, rotating on insert and delete to restore that, so
/// the tree is never deeper than about 1.44 log2(n).
///
/// <para><b>Why it matters for telemetry.</b> Readings arrive almost in timestamp order.
/// A plain BST fed sorted keys turns into a linked list - every insert and every lookup
/// walks the whole thing. The AVL rotations stop that: 50,000 readings sit at most ~23
/// levels deep, whatever order they came in.</para>
///
/// <para><b>Range queries.</b> <see cref="Range"/> walks the tree in order but skips any
/// subtree that lies wholly outside [from, to]. Pulling two hours out of a day of history
/// visits O(log n + k) nodes, where k is the number of readings returned, instead of
/// scanning all n.</para>
///
/// <para>Not thread-safe: the caller locks, the same as the sorted-dictionary timeline.</para>
/// </summary>
// Reference: Adelson-Velsky, G. and Landis, E. (1962). An algorithm for the organization of information.
// Reference: Wikipedia. AVL tree. https://en.wikipedia.org/wiki/AVL_tree
public sealed class AvlTree<TKey, TValue> where TKey : IComparable<TKey>
{
    private sealed class Node(TKey key, TValue value)
    {
        public readonly TKey Key = key;
        public TValue Value = value;
        public Node? Left;
        public Node? Right;
        public int Height = 1;
    }

    private Node? _root;

    public int Count { get; private set; }

    /// <summary>Levels from the root to the deepest leaf. Zero when empty.</summary>
    public int Height => HeightOf(_root);

    // ---- insert ------------------------------------------------------------

    /// <summary>Inserts the key, or replaces its value if the key is already present. O(log n).</summary>
    public void Add(TKey key, TValue value) => _root = Insert(_root, key, value);

    private Node Insert(Node? node, TKey key, TValue value)
    {
        if (node is null)
        {
            Count++;
            return new Node(key, value);
        }

        var compare = key.CompareTo(node.Key);
        if (compare < 0) node.Left = Insert(node.Left, key, value);
        else if (compare > 0) node.Right = Insert(node.Right, key, value);
        else
        {
            node.Value = value;
            return node;
        }

        return Rebalance(node);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // ---- delete oldest ------------------------------------------------------

    /// <summary>Removes the smallest key - for a timestamp tree, the oldest reading. O(log n).</summary>
    public bool TryRemoveMin(out TKey key, out TValue value)
    {
        if (_root is null)
        {
            key = default!;
            value = default!;
            return false;
        }

        var min = _root;
        while (min.Left is not null) min = min.Left;
        key = min.Key;
        value = min.Value;

        _root = RemoveMin(_root);
        Count--;
        return true;
    }

    private static Node? RemoveMin(Node node)
    {
        if (node.Left is null) return node.Right;
        node.Left = RemoveMin(node.Left);
        return Rebalance(node);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // ---- range query -------------------------------------------------------

    /// <summary>
    /// Every value whose key lies in [<paramref name="from"/>, <paramref name="to"/>], in key
    /// order. <paramref name="visited"/> counts the nodes touched, which is the evidence that
    /// the query pruned the tree rather than scanning it.
    /// </summary>
    public List<TValue> Range(TKey from, TKey to, out int visited)
    {
        var into = new List<TValue>();
        visited = 0;
        if (from.CompareTo(to) <= 0) Collect(_root, from, to, into, ref visited);
        return into;
    }

    private static void Collect(Node? node, TKey from, TKey to, List<TValue> into, ref int visited)
    {
        if (node is null) return;
        visited++;

        var atOrAfterFrom = node.Key.CompareTo(from) >= 0;
        var atOrBeforeTo = node.Key.CompareTo(to) <= 0;

        // Only go left if this key is still at or past `from` - everything left is smaller.
        if (atOrAfterFrom) Collect(node.Left, from, to, into, ref visited);
        if (atOrAfterFrom && atOrBeforeTo) into.Add(node.Value);
        // Only go right if this key is still at or before `to` - everything right is larger.
        if (atOrBeforeTo) Collect(node.Right, from, to, into, ref visited);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // ---- balancing ---------------------------------------------------------

    private static int HeightOf(Node? node) => node?.Height ?? 0;

    private static int BalanceOf(Node node) => HeightOf(node.Left) - HeightOf(node.Right);

    private static void UpdateHeight(Node node) => node.Height = 1 + Math.Max(HeightOf(node.Left), HeightOf(node.Right));

    //      y            x
    //     / \          / \
    //    x   C  ->    A   y
    //   / \              / \
    //  A   B            B   C
    private static Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        UpdateHeight(y);
        UpdateHeight(x);
        return x;
    }

    private static Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        UpdateHeight(x);
        UpdateHeight(y);
        return y;
    }

    /// <summary>Restores the AVL property at one node: the four cases LL, LR, RR, RL.</summary>
    private static Node Rebalance(Node node)
    {
        UpdateHeight(node);
        var balance = BalanceOf(node);

        if (balance > 1)
        {
            if (BalanceOf(node.Left!) < 0) node.Left = RotateLeft(node.Left!);    // LR
            return RotateRight(node);                                              // LL
        }

        if (balance < -1)
        {
            if (BalanceOf(node.Right!) > 0) node.Right = RotateRight(node.Right!); // RL
            return RotateLeft(node);                                               // RR
        }

        return node;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    // ---- self-check --------------------------------------------------------

    /// <summary>True if every node is in BST order, has a correct stored height, and is balanced.</summary>
    public bool IsValid()
    {
        Node? previous = null;
        return Validate(_root, ref previous, out _);
    }

    // In-order walk: every key must be larger than the one visited before it.
    private static bool Validate(Node? node, ref Node? previous, out int height)
    {
        height = 0;
        if (node is null) return true;

        if (!Validate(node.Left, ref previous, out var left)) return false;
        if (previous is not null && previous.Key.CompareTo(node.Key) >= 0) return false;
        previous = node;
        if (!Validate(node.Right, ref previous, out var right)) return false;

        height = 1 + Math.Max(left, right);
        return node.Height == height && Math.Abs(left - right) <= 1;
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
