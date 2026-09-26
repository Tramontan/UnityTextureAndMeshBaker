using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LodCreator
{
    /// <summary>
    /// Quadric-error (QEM) edge-collapse mesh simplifier built for texture atlases:
    ///
    /// - Half-edge collapses only (a vertex moves onto an existing neighbour), so every output
    ///   vertex is an untouched original vertex: UVs are never interpolated and can't leave their
    ///   atlas cell; normals, tangents, colours and lightmap UVs are preserved exactly.
    /// - Topology is built on position-welded vertices, while each triangle corner keeps its
    ///   "wedge" (original vertex + submesh). A collapse is only allowed if every wedge of the
    ///   removed vertex has a counterpart across the collapsed edge - which means UV seams, hard
    ///   normal edges and material borders can only slide along themselves, never tear.
    /// - Open borders slide only along the border; flips, degenerate faces and non-manifold
    ///   results (link condition) are rejected.
    /// - An optional error limit (in mesh units) stops simplification before the triangle target
    ///   when the next collapse would move the surface further than that - so a LOD never gets
    ///   visibly wrong just to hit a percentage (thin arches turning into "sails" etc.). It is a
    ///   two-sided maximum: removed original vertices must stay within the limit of the new surface,
    ///   and new triangles (centre and edge midpoints) within the limit of the original surface.
    /// - If the strict pass can't reach the target (hard-edged low-poly models, where corners of
    ///   three or more seams are locked), a second pass also lets seams move: a corner that loses
    ///   its wedge takes the closest one (UV/normal, same submesh) of the target vertex, and the
    ///   attribute error is added to the collapse cost, so the least visible ones go first.
    /// </summary>
    public static class MeshSimplifier
    {
        private const float BorderWeight = 16f;
        private const float SeamWeight = 4f;
        private const double MinFlipDot = 0.2;
        private const double UvPenaltyWeight = 16.0;
        private const double NormalPenaltyWeight = 0.1;

        // Attribute error of one triangle corner, in units of squared mesh extent: a UV jump of 0.1
        // costs about as much as moving a surface by 1.3% of the object size.
        private const double PenaltyScale = 1e-3;

        /// <summary>Changes whenever the output for the same input changes (part of cached LOD mesh names).</summary>
        public const int Version = 7;

        /// <summary>
        /// Returns a new mesh with about <paramref name="ratio"/> of the triangles, or more if reaching
        /// it would need a surface error above <paramref name="maxError"/> (mesh units, 0 = no limit).
        /// A copy is returned if the mesh can't be simplified.
        /// </summary>
        public static Mesh Simplify(Mesh source, float ratio, float maxError, out int sourceTriangles, out int resultTriangles)
        {
            var state = new State(source);
            sourceTriangles = state.AliveTriangles;
            if (state.Valid)
            {
                int target = Mathf.Max(1, Mathf.RoundToInt(state.AliveTriangles * Mathf.Clamp01(ratio)));
                state.Run(target, maxError);
            }

            resultTriangles = state.AliveTriangles;
            return state.Valid ? state.Build(source) : Object.Instantiate(source);
        }

        private struct Candidate
        {
            public double cost;
            public int from;
            public int to;
            public int stampFrom;
            public int stampTo;
            public bool evaluated;
        }

        private sealed class CandidateHeap
        {
            private Candidate[] _items = new Candidate[1024];
            public int Count { get; private set; }

            public void Push(Candidate item)
            {
                if (Count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                int i = Count++;
                while (i > 0)
                {
                    int parent = (i - 1) >> 1;
                    if (_items[parent].cost <= item.cost) break;
                    _items[i] = _items[parent];
                    i = parent;
                }

                _items[i] = item;
            }

            public Candidate Pop()
            {
                Candidate top = _items[0];
                Candidate last = _items[--Count];
                int i = 0;
                while (true)
                {
                    int child = i * 2 + 1;
                    if (child >= Count) break;
                    if (child + 1 < Count && _items[child + 1].cost < _items[child].cost) child++;
                    if (_items[child].cost >= last.cost) break;
                    _items[i] = _items[child];
                    i = child;
                }

                if (Count > 0) _items[i] = last;
                return top;
            }
        }

        private sealed class State
        {
            public bool Valid { get; } = true;
            public int AliveTriangles { get; private set; }

            private readonly int _vertexCount;
            private readonly int _subMeshCount;

            // Welded geometry vertices.
            private readonly Vector3[] _pos;
            private readonly double[] _q;
            private double _maxErrorSq;

            // Original vertex positions (welded ids) each surviving vertex stands for.
            private readonly List<int>[] _represented;
            private readonly List<Vector3> _fan = new List<Vector3>();
            private readonly List<int> _checkVerts = new List<int>();

            // Original topology, for the new-surface-to-original distance.
            private readonly int[] _originalTg;
            private readonly List<int>[] _originalVertTris;
            private readonly int[] _originalMark;
            private int _markStamp;
            private readonly List<int> _nearOriginal = new List<int>();
            private readonly HashSet<int> _checkTris = new HashSet<int>();
            private readonly bool[] _alive;
            private readonly int[] _stamp;
            private readonly List<int>[] _vertTris;

            // Triangles: geometry vertex and wedge per corner.
            private readonly int[] _tg;
            private readonly int[] _tw;
            private readonly int[] _tsub;
            private readonly bool[] _talive;

            // Wedge = (original vertex, submesh).
            private readonly List<int> _wedgeVertex = new List<int>();
            private readonly List<int> _wedgeSub = new List<int>();

            // Original vertex attributes compared when seams may move (permissive pass).
            private readonly Vector2[] _uv;
            private readonly Vector3[] _normals;
            private readonly double _extentSq;
            private bool _permissive;
            private readonly Dictionary<int, double> _looseWedges = new Dictionary<int, double>();

            private readonly CandidateHeap _heap = new CandidateHeap();
            private readonly HashSet<int> _setA = new HashSet<int>();
            private readonly HashSet<int> _setB = new HashSet<int>();
            private readonly List<int> _edgeTris = new List<int>();
            private readonly List<int> _otherTris = new List<int>();
            private readonly Dictionary<int, int> _wedgeMap = new Dictionary<int, int>();

            public State(Mesh mesh)
            {
                _subMeshCount = mesh.subMeshCount;
                for (int s = 0; s < _subMeshCount; s++)
                {
                    if (mesh.GetTopology(s) != MeshTopology.Triangles)
                    {
                        Valid = false;
                        return;
                    }
                }

                Vector3[] vertices = mesh.vertices;
                _vertexCount = vertices.Length;
                _uv = mesh.uv;
                _normals = mesh.normals;

                // Weld by position so that seams/hard edges are topologically connected.
                Bounds bounds = mesh.bounds;
                double extent = Math.Max(bounds.size.x, Math.Max(bounds.size.y, bounds.size.z));
                _extentSq = Math.Max(extent * extent, 1e-12);
                double quant = Math.Max(extent * 1e-6, 1e-7);
                var weld = new Dictionary<(long, long, long), int>();
                var positions = new List<Vector3>();
                var geomOf = new int[vertices.Length];
                for (int i = 0; i < vertices.Length; i++)
                {
                    Vector3 v = vertices[i];
                    var key = ((long)Math.Round(v.x / quant), (long)Math.Round(v.y / quant), (long)Math.Round(v.z / quant));
                    if (!weld.TryGetValue(key, out int g))
                    {
                        g = positions.Count;
                        weld[key] = g;
                        positions.Add(v);
                    }

                    geomOf[i] = g;
                }

                _pos = positions.ToArray();
                int geomCount = _pos.Length;
                _q = new double[geomCount * 10];
                _represented = new List<int>[geomCount];
                _alive = new bool[geomCount];
                _stamp = new int[geomCount];
                _vertTris = new List<int>[geomCount];
                for (int g = 0; g < geomCount; g++)
                {
                    _alive[g] = true;
                    _vertTris[g] = new List<int>(8);
                    _represented[g] = new List<int>(1) { g };
                }

                var tg = new List<int>();
                var tw = new List<int>();
                var tsub = new List<int>();
                var wedgeIndex = new Dictionary<long, int>();

                int Wedge(int vertex, int submesh)
                {
                    long key = (long)vertex * _subMeshCount + submesh;
                    if (!wedgeIndex.TryGetValue(key, out int w))
                    {
                        w = _wedgeVertex.Count;
                        wedgeIndex[key] = w;
                        _wedgeVertex.Add(vertex);
                        _wedgeSub.Add(submesh);
                    }

                    return w;
                }

                for (int s = 0; s < _subMeshCount; s++)
                {
                    int[] indices = mesh.GetIndices(s);
                    for (int k = 0; k + 2 < indices.Length; k += 3)
                    {
                        int a = indices[k], b = indices[k + 1], c = indices[k + 2];
                        int ga = geomOf[a], gb = geomOf[b], gc = geomOf[c];
                        if (ga == gb || gb == gc || ga == gc) continue; // degenerate after welding - invisible

                        tg.Add(ga); tg.Add(gb); tg.Add(gc);
                        tw.Add(Wedge(a, s)); tw.Add(Wedge(b, s)); tw.Add(Wedge(c, s));
                        tsub.Add(s);
                    }
                }

                _tg = tg.ToArray();
                _tw = tw.ToArray();
                _tsub = tsub.ToArray();
                int triCount = _tsub.Length;
                _talive = new bool[triCount];
                for (int t = 0; t < triCount; t++)
                {
                    _talive[t] = true;
                    _vertTris[_tg[t * 3]].Add(t);
                    _vertTris[_tg[t * 3 + 1]].Add(t);
                    _vertTris[_tg[t * 3 + 2]].Add(t);
                }

                _originalTg = (int[])_tg.Clone();
                _originalMark = new int[triCount];
                _originalVertTris = new List<int>[geomCount];
                for (int g = 0; g < geomCount; g++) _originalVertTris[g] = new List<int>(_vertTris[g]);

                AliveTriangles = triCount;
                InitQuadrics();
                InitCandidates();
            }

            // ---------------- quadrics

            private void AddPlane(int g, double nx, double ny, double nz, double d, double w)
            {
                int o = g * 10;
                _q[o] += w * nx * nx; _q[o + 1] += w * nx * ny; _q[o + 2] += w * nx * nz; _q[o + 3] += w * nx * d;
                _q[o + 4] += w * ny * ny; _q[o + 5] += w * ny * nz; _q[o + 6] += w * ny * d;
                _q[o + 7] += w * nz * nz; _q[o + 8] += w * nz * d;
                _q[o + 9] += w * d * d;
            }

            private double Cost(int from, int to)
            {
                int a = from * 10, b = to * 10;
                Vector3 p = _pos[to];
                double x = p.x, y = p.y, z = p.z;
                double q0 = _q[a] + _q[b], q1 = _q[a + 1] + _q[b + 1], q2 = _q[a + 2] + _q[b + 2], q3 = _q[a + 3] + _q[b + 3];
                double q4 = _q[a + 4] + _q[b + 4], q5 = _q[a + 5] + _q[b + 5], q6 = _q[a + 6] + _q[b + 6];
                double q7 = _q[a + 7] + _q[b + 7], q8 = _q[a + 8] + _q[b + 8], q9 = _q[a + 9] + _q[b + 9];
                return q0 * x * x + 2 * q1 * x * y + 2 * q2 * x * z + 2 * q3 * x
                       + q4 * y * y + 2 * q5 * y * z + 2 * q6 * y
                       + q7 * z * z + 2 * q8 * z + q9;
            }

            private void TriangleNormal(int t, out double nx, out double ny, out double nz, out double length)
            {
                Vector3 p0 = _pos[_tg[t * 3]], p1 = _pos[_tg[t * 3 + 1]], p2 = _pos[_tg[t * 3 + 2]];
                Cross(p1 - p0, p2 - p0, out nx, out ny, out nz);
                length = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            }

            private static void Cross(Vector3 u, Vector3 v, out double x, out double y, out double z)
            {
                x = (double)u.y * v.z - (double)u.z * v.y;
                y = (double)u.z * v.x - (double)u.x * v.z;
                z = (double)u.x * v.y - (double)u.y * v.x;
            }

            private void InitQuadrics()
            {
                for (int t = 0; t < _tsub.Length; t++)
                {
                    TriangleNormal(t, out double nx, out double ny, out double nz, out double length);
                    if (length < 1e-20) continue;

                    nx /= length; ny /= length; nz /= length;
                    Vector3 p0 = _pos[_tg[t * 3]];
                    double d = -(nx * p0.x + ny * p0.y + nz * p0.z);
                    // Unit weight per face (not area): thin parts (poles, wires, arches) cost as much to
                    // destroy as big flat faces, otherwise they collapse into "sails" first.
                    for (int c = 0; c < 3; c++) AddPlane(_tg[t * 3 + c], nx, ny, nz, d, 1.0);
                }

                // Borders and seams get perpendicular constraint planes so their shape is kept.
                var done = new HashSet<long>();
                for (int t = 0; t < _tsub.Length; t++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        int ga = _tg[t * 3 + c];
                        int gb = _tg[t * 3 + (c + 1) % 3];
                        long key = ga < gb ? ((long)ga << 32) | (uint)gb : ((long)gb << 32) | (uint)ga;
                        if (!done.Add(key)) continue;

                        int count = 0;
                        bool seam = false;
                        int firstA = -1, firstB = -1;
                        foreach (int other in _vertTris[ga])
                        {
                            int cb = CornerOf(other, gb);
                            if (cb < 0) continue;
                            int ca = CornerOf(other, ga);
                            count++;
                            if (firstA < 0) { firstA = _tw[other * 3 + ca]; firstB = _tw[other * 3 + cb]; }
                            else if (_tw[other * 3 + ca] != firstA || _tw[other * 3 + cb] != firstB) seam = true;
                        }

                        float weight = count == 1 ? BorderWeight : seam ? SeamWeight : 0f;
                        if (weight <= 0f) continue;

                        TriangleNormal(t, out double nx, out double ny, out double nz, out double length);
                        if (length < 1e-20) continue;

                        Vector3 pa = _pos[ga], pb = _pos[gb];
                        Vector3 edge = pb - pa;
                        Cross(edge, new Vector3((float)(nx / length), (float)(ny / length), (float)(nz / length)), out double bx, out double by, out double bz);
                        double bl = Math.Sqrt(bx * bx + by * by + bz * bz);
                        if (bl < 1e-20) continue;

                        bx /= bl; by /= bl; bz /= bl;
                        double d = -(bx * pa.x + by * pa.y + bz * pa.z);
                        double w = weight;
                        AddPlane(ga, bx, by, bz, d, w);
                        AddPlane(gb, bx, by, bz, d, w);
                    }
                }
            }

            // ---------------- candidates

            private void InitCandidates()
            {
                var done = new HashSet<long>();
                for (int t = 0; t < _tsub.Length; t++)
                {
                    if (!_talive[t]) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int ga = _tg[t * 3 + c];
                        int gb = _tg[t * 3 + (c + 1) % 3];
                        long key = ga < gb ? ((long)ga << 32) | (uint)gb : ((long)gb << 32) | (uint)ga;
                        if (!done.Add(key)) continue;

                        PushCandidate(ga, gb);
                        PushCandidate(gb, ga);
                    }
                }
            }

            private void PushCandidate(int from, int to)
            {
                _heap.Push(new Candidate
                {
                    cost = Cost(from, to),
                    from = from,
                    to = to,
                    stampFrom = _stamp[from],
                    stampTo = _stamp[to]
                });
            }

            public void Run(int targetTriangles, float maxError)
            {
                _maxErrorSq = maxError > 0f ? (double)maxError * maxError : 0;
                Drain(targetTriangles);
                if (AliveTriangles <= targetTriangles) return;

                // The strict pass is stuck: let seams move, paying for the attribute error.
                _permissive = true;
                InitCandidates();
                Drain(targetTriangles);
            }

            private void Drain(int targetTriangles)
            {
                while (AliveTriangles > targetTriangles && _heap.Count > 0)
                {
                    Candidate c = _heap.Pop();
                    if (!_alive[c.from] || !_alive[c.to] || _stamp[c.from] != c.stampFrom || _stamp[c.to] != c.stampTo) continue;

                    if (_permissive && !c.evaluated)
                    {
                        // Heap costs are geometric only: add the seam penalty lazily and re-queue.
                        Gather(c.from, c.to);
                        if (_edgeTris.Count == 0 || !BuildWedgeMap(c.from, c.to, out double penalty)) continue;
                        if (penalty > 0)
                        {
                            c.cost += penalty;
                            c.evaluated = true;
                            _heap.Push(c);
                            continue;
                        }
                    }

                    TryCollapse(c.from, c.to);
                }
            }

            // ---------------- collapse

            private int CornerOf(int t, int g)
            {
                int o = t * 3;
                if (_tg[o] == g) return 0;
                if (_tg[o + 1] == g) return 1;
                if (_tg[o + 2] == g) return 2;
                return -1;
            }

            private void CompactTris(int g)
            {
                List<int> list = _vertTris[g];
                list.RemoveAll(t => !_talive[t]);
            }

            private void CollectNeighbours(int g, HashSet<int> set)
            {
                set.Clear();
                foreach (int t in _vertTris[g])
                {
                    if (!_talive[t]) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int other = _tg[t * 3 + c];
                        if (other != g) set.Add(other);
                    }
                }
            }

            private bool IsBorderVertex(int g)
            {
                foreach (int t in _vertTris[g])
                {
                    if (!_talive[t]) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int other = _tg[t * 3 + c];
                        if (other == g) continue;

                        int count = 0;
                        foreach (int t2 in _vertTris[g])
                        {
                            if (_talive[t2] && CornerOf(t2, other) >= 0) count++;
                        }

                        if (count == 1) return true;
                    }
                }

                return false;
            }

            private void Gather(int a, int b)
            {
                CompactTris(a);
                _edgeTris.Clear();
                _otherTris.Clear();
                foreach (int t in _vertTris[a])
                {
                    if (CornerOf(t, b) >= 0) _edgeTris.Add(t);
                    else _otherTris.Add(t);
                }
            }

            /// <summary>
            /// Maps every wedge of <paramref name="a"/> to a wedge of <paramref name="b"/>. Strict: only
            /// across the collapsed edge (seams slide along themselves, no attribute error). Permissive:
            /// an unmatched wedge takes b's closest wedge of the same submesh; <paramref name="penalty"/>
            /// is the attribute error summed over the triangle corners that change attributes.
            /// </summary>
            private bool BuildWedgeMap(int a, int b, out double penalty)
            {
                penalty = 0;
                _wedgeMap.Clear();
                _looseWedges.Clear();
                foreach (int t in _edgeTris)
                {
                    int wa = _tw[t * 3 + CornerOf(t, a)];
                    int wb = _tw[t * 3 + CornerOf(t, b)];
                    if (_wedgeMap.TryGetValue(wa, out int existing))
                    {
                        if (existing == wb) continue;
                        if (!_permissive) return false;
                        penalty += AttributeDistance(wb, existing) * _extentSq * PenaltyScale;
                        continue;
                    }

                    _wedgeMap[wa] = wb;
                }

                foreach (int t in _otherTris)
                {
                    int wa = _tw[t * 3 + CornerOf(t, a)];
                    if (!_wedgeMap.ContainsKey(wa))
                    {
                        if (!_permissive) return false;

                        int best = -1;
                        double bestDistance = double.MaxValue;
                        foreach (int tb in _vertTris[b])
                        {
                            if (!_talive[tb]) continue;
                            int wb = _tw[tb * 3 + CornerOf(tb, b)];
                            if (_wedgeSub[wb] != _wedgeSub[wa]) continue;

                            double distance = AttributeDistance(wa, wb);
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                                best = wb;
                            }
                        }

                        if (best < 0) return false; // b has no corner of this material
                        _wedgeMap[wa] = best;
                        _looseWedges[wa] = bestDistance;
                    }

                    if (_looseWedges.TryGetValue(wa, out double error)) penalty += error * _extentSq * PenaltyScale;
                }

                return true;
            }

            private double AttributeDistance(int wa, int wb)
            {
                int va = _wedgeVertex[wa], vb = _wedgeVertex[wb];
                double distance = 1e-9;
                if (_uv.Length > 0) distance += (_uv[va] - _uv[vb]).sqrMagnitude * UvPenaltyWeight;
                if (_normals.Length > 0) distance += (_normals[va] - _normals[vb]).sqrMagnitude * NormalPenaltyWeight;
                return distance;
            }

            private bool TryCollapse(int a, int b)
            {
                Gather(a, b);
                if (_edgeTris.Count == 0) return false;

                // A border vertex may only slide along a border edge.
                if (_edgeTris.Count != 1 && IsBorderVertex(a)) return false;

                // Link condition: shared neighbours must be exactly the edge triangles' third vertices.
                CollectNeighbours(a, _setA);
                CollectNeighbours(b, _setB);
                int common = 0;
                foreach (int n in _setB)
                {
                    if (_setA.Contains(n)) common++;
                }

                if (common > _edgeTris.Count) return false;

                // Wedge mapping (keeps UV seams / hard edges / material borders intact in the strict pass).
                if (!BuildWedgeMap(a, b, out _)) return false;

                // No flipped or collapsed faces.
                Vector3 target = _pos[b];
                foreach (int t in _otherTris)
                {
                    int ca = CornerOf(t, a);
                    Vector3 p0 = _pos[_tg[t * 3]], p1 = _pos[_tg[t * 3 + 1]], p2 = _pos[_tg[t * 3 + 2]];
                    Cross(p1 - p0, p2 - p0, out double ox, out double oy, out double oz);
                    if (ca == 0) p0 = target; else if (ca == 1) p1 = target; else p2 = target;
                    Cross(p1 - p0, p2 - p0, out double nx, out double ny, out double nz);

                    double oldLen = Math.Sqrt(ox * ox + oy * oy + oz * oz);
                    double newLen = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (newLen <= 1e-12 * Math.Max(oldLen, 1e-12)) return false;
                    if (oldLen > 1e-20 && (ox * nx + oy * ny + oz * nz) / (oldLen * newLen) < MinFlipDot) return false;
                }

                if (_maxErrorSq > 0 && !WithinError(a, b)) return false;

                // Commit.
                foreach (int t in _edgeTris)
                {
                    _talive[t] = false;
                    AliveTriangles--;
                }

                foreach (int t in _otherTris)
                {
                    int c = t * 3 + CornerOf(t, a);
                    _tg[c] = b;
                    _tw[c] = _wedgeMap[_tw[c]];
                    _vertTris[b].Add(t);
                }

                int qa = a * 10, qb = b * 10;
                for (int i = 0; i < 10; i++) _q[qb + i] += _q[qa + i];
                _represented[b].AddRange(_represented[a]);
                _represented[a] = null;

                _alive[a] = false;
                _vertTris[a].Clear();
                _stamp[b]++;
                CompactTris(b);

                CollectNeighbours(b, _setB);
                foreach (int n in _setB)
                {
                    PushCandidate(b, n);
                    PushCandidate(n, b);
                }

                return true;
            }

            /// <summary>
            /// Would every original point represented by a, b and a's neighbours stay within the error
            /// limit of the triangles around them after the collapse? Neighbours are included because
            /// their triangles change shape too - otherwise error creeps up over many collapses.
            /// </summary>
            private bool WithinError(int a, int b)
            {
                // _setA holds a's neighbours (filled by the link-condition check).
                _checkVerts.Clear();
                _checkVerts.Add(a);
                foreach (int n in _setA) _checkVerts.Add(n);

                _checkTris.Clear();
                foreach (int g in _checkVerts)
                {
                    foreach (int t in _vertTris[g])
                    {
                        if (_talive[t] && !_edgeTris.Contains(t)) _checkTris.Add(t);
                    }
                }

                _fan.Clear();
                Vector3 target = _pos[b];
                foreach (int t in _checkTris)
                {
                    int o = t * 3;
                    _fan.Add(_tg[o] == a ? target : _pos[_tg[o]]);
                    _fan.Add(_tg[o + 1] == a ? target : _pos[_tg[o + 1]]);
                    _fan.Add(_tg[o + 2] == a ? target : _pos[_tg[o + 2]]);
                }

                foreach (int g in _checkVerts)
                {
                    if (!PointsWithinError(_represented[g], g == a ? target : _pos[g])) return false;
                }

                // The other direction: a new triangle must not span empty space ("sails" across a
                // curved tube lie flat against the original points, so the check above misses them).
                // The original surface near a triangle = original triangles around the points its
                // corners represent.
                int index = 0;
                foreach (int t in _checkTris)
                {
                    if (!TriangleNearOriginal(t, a, b, index)) return false;
                    index += 3;
                }

                return true;
            }

            private bool TriangleNearOriginal(int t, int a, int b, int fanIndex)
            {
                _markStamp++;
                _nearOriginal.Clear();
                for (int c = 0; c < 3; c++)
                {
                    int g = _tg[t * 3 + c];
                    CollectOriginal(g);
                    if (g == a) CollectOriginal(b);
                    else if (g == b) CollectOriginal(a);
                }

                Vector3 p0 = _fan[fanIndex], p1 = _fan[fanIndex + 1], p2 = _fan[fanIndex + 2];
                return NearOriginal((p0 + p1 + p2) / 3f) &&
                       NearOriginal((p0 + p1) * 0.5f) &&
                       NearOriginal((p1 + p2) * 0.5f) &&
                       NearOriginal((p2 + p0) * 0.5f);
            }

            private void CollectOriginal(int g)
            {
                foreach (int point in _represented[g])
                {
                    foreach (int ot in _originalVertTris[point])
                    {
                        if (_originalMark[ot] == _markStamp) continue;
                        _originalMark[ot] = _markStamp;
                        _nearOriginal.Add(ot);
                    }
                }
            }

            private bool NearOriginal(Vector3 p)
            {
                foreach (int ot in _nearOriginal)
                {
                    int o = ot * 3;
                    Vector3 closest = ClosestPointOnTriangle(p, _pos[_originalTg[o]], _pos[_originalTg[o + 1]], _pos[_originalTg[o + 2]]);
                    if ((p - closest).sqrMagnitude <= _maxErrorSq) return true;
                }

                return false;
            }

            private bool PointsWithinError(List<int> points, Vector3 target)
            {
                foreach (int g in points)
                {
                    Vector3 p = _pos[g]; // half-edge collapses never move vertices: _pos holds the originals
                    double best = (p - target).sqrMagnitude; // no triangles left: distance to the merged vertex
                    for (int i = 0; i < _fan.Count && best > _maxErrorSq; i += 3)
                    {
                        best = Math.Min(best, (p - ClosestPointOnTriangle(p, _fan[i], _fan[i + 1], _fan[i + 2])).sqrMagnitude);
                    }

                    if (best > _maxErrorSq) return false;
                }

                return true;
            }

            private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
            {
                Vector3 ab = b - a, ac = c - a, ap = p - a;
                float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
                if (d1 <= 0f && d2 <= 0f) return a;

                Vector3 bp = p - b;
                float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
                if (d3 >= 0f && d4 <= d3) return b;

                float vc = d1 * d4 - d3 * d2;
                if (vc <= 0f && d1 >= 0f && d3 <= 0f) return a + ab * (d1 / (d1 - d3));

                Vector3 cp = p - c;
                float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
                if (d6 >= 0f && d5 <= d6) return c;

                float vb = d5 * d2 - d1 * d6;
                if (vb <= 0f && d2 >= 0f && d6 <= 0f) return a + ac * (d2 / (d2 - d6));

                float va = d3 * d6 - d5 * d4;
                if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f) return b + (c - b) * ((d4 - d3) / (d4 - d3 + (d5 - d6)));

                float denom = va + vb + vc;
                if (Mathf.Abs(denom) < 1e-30f) return a;
                float v = vb / denom, w = vc / denom;
                return a + ab * v + ac * w;
            }

            // ---------------- output

            public Mesh Build(Mesh source)
            {
                var remap = new int[_vertexCount];
                for (int i = 0; i < remap.Length; i++) remap[i] = -1;
                var order = new List<int>();
                var subTris = new List<int>[_subMeshCount];
                for (int s = 0; s < _subMeshCount; s++) subTris[s] = new List<int>();

                for (int t = 0; t < _tsub.Length; t++)
                {
                    if (!_talive[t]) continue;
                    for (int c = 0; c < 3; c++)
                    {
                        int v = _wedgeVertex[_tw[t * 3 + c]];
                        if (remap[v] < 0)
                        {
                            remap[v] = order.Count;
                            order.Add(v);
                        }

                        subTris[_tsub[t]].Add(remap[v]);
                    }
                }

                var mesh = new Mesh
                {
                    name = source.name + "_LOD",
                    indexFormat = order.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16
                };

                var v3 = new List<Vector3>();
                source.GetVertices(v3);
                mesh.SetVertices(Pick(v3, order));

                if (source.HasVertexAttribute(VertexAttribute.Normal))
                {
                    source.GetNormals(v3);
                    mesh.SetNormals(Pick(v3, order));
                }

                if (source.HasVertexAttribute(VertexAttribute.Tangent))
                {
                    var v4 = new List<Vector4>();
                    source.GetTangents(v4);
                    mesh.SetTangents(Pick(v4, order));
                }

                if (source.HasVertexAttribute(VertexAttribute.Color))
                {
                    var colors = new List<Color32>();
                    source.GetColors(colors);
                    mesh.SetColors(Pick(colors, order));
                }

                for (int channel = 0; channel < 8; channel++)
                {
                    var attribute = (VertexAttribute)((int)VertexAttribute.TexCoord0 + channel);
                    if (!source.HasVertexAttribute(attribute)) continue;

                    int dimension = source.GetVertexAttributeDimension(attribute);
                    var uvs = new List<Vector4>();
                    source.GetUVs(channel, uvs);
                    List<Vector4> picked = Pick(uvs, order);
                    switch (dimension)
                    {
                        case 2: mesh.SetUVs(channel, picked.ConvertAll(u => new Vector2(u.x, u.y))); break;
                        case 3: mesh.SetUVs(channel, picked.ConvertAll(u => new Vector3(u.x, u.y, u.z))); break;
                        default: mesh.SetUVs(channel, picked); break;
                    }
                }

                mesh.subMeshCount = _subMeshCount;
                for (int s = 0; s < _subMeshCount; s++)
                {
                    mesh.SetTriangles(subTris[s], s, false);
                }

                mesh.RecalculateBounds();
                return mesh;
            }

            private static List<T> Pick<T>(List<T> source, List<int> order)
            {
                var result = new List<T>(order.Count);
                foreach (int index in order) result.Add(source[index]);
                return result;
            }
        }
    }
}
