using System;
using System.Collections.Generic;
using UnityEngine;

namespace TexturesBaker
{
    /// <summary>
    /// MaxRects bin packer (Best Short Side Fit, no rotation - rotating a cell would also mean
    /// rotating mesh UVs). Deterministic, so the window preview and the actual bake always agree.
    /// </summary>
    public sealed class MaxRectsPacker
    {
        private readonly List<RectInt> _free = new List<RectInt>();

        public MaxRectsPacker(int width, int height)
        {
            _free.Add(new RectInt(0, 0, width, height));
        }

        public bool TryInsert(int width, int height, out RectInt placed)
        {
            placed = default;
            int bestShort = int.MaxValue;
            int bestLong = int.MaxValue;
            bool found = false;

            foreach (RectInt free in _free)
            {
                if (width > free.width || height > free.height)
                {
                    continue;
                }

                int leftoverX = free.width - width;
                int leftoverY = free.height - height;
                int shortSide = Math.Min(leftoverX, leftoverY);
                int longSide = Math.Max(leftoverX, leftoverY);

                if (shortSide < bestShort || (shortSide == bestShort && longSide < bestLong))
                {
                    bestShort = shortSide;
                    bestLong = longSide;
                    placed = new RectInt(free.x, free.y, width, height);
                    found = true;
                }
            }

            if (!found)
            {
                return false;
            }

            SplitFreeRects(placed);
            PruneFreeRects();
            return true;
        }

        private void SplitFreeRects(RectInt used)
        {
            for (int i = _free.Count - 1; i >= 0; i--)
            {
                RectInt free = _free[i];
                if (!Overlaps(free, used))
                {
                    continue;
                }

                _free.RemoveAt(i);

                if (used.x > free.x)
                {
                    _free.Add(new RectInt(free.x, free.y, used.x - free.x, free.height));
                }

                if (used.xMax < free.xMax)
                {
                    _free.Add(new RectInt(used.xMax, free.y, free.xMax - used.xMax, free.height));
                }

                if (used.y > free.y)
                {
                    _free.Add(new RectInt(free.x, free.y, free.width, used.y - free.y));
                }

                if (used.yMax < free.yMax)
                {
                    _free.Add(new RectInt(free.x, used.yMax, free.width, free.yMax - used.yMax));
                }
            }
        }

        private void PruneFreeRects()
        {
            for (int i = 0; i < _free.Count; i++)
            {
                for (int j = i + 1; j < _free.Count; j++)
                {
                    if (Contains(_free[j], _free[i]))
                    {
                        _free.RemoveAt(i);
                        i--;
                        break;
                    }

                    if (Contains(_free[i], _free[j]))
                    {
                        _free.RemoveAt(j);
                        j--;
                    }
                }
            }
        }

        private static bool Overlaps(RectInt a, RectInt b)
        {
            return a.x < b.xMax && a.xMax > b.x && a.y < b.yMax && a.yMax > b.y;
        }

        private static bool Contains(RectInt outer, RectInt inner)
        {
            return inner.x >= outer.x && inner.y >= outer.y && inner.xMax <= outer.xMax && inner.yMax <= outer.yMax;
        }
    }
}
