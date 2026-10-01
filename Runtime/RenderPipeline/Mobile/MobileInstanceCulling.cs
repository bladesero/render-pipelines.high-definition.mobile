namespace UnityEngine.Rendering.HighDefinition
{
    internal static class MobileInstanceCulling
    {
        internal static Bounds TransformBounds(Bounds local, Matrix4x4 matrix, float padding)
        {
            Vector3 e = local.extents;
            var extent = new Vector3(
                Mathf.Abs(matrix.m00) * e.x + Mathf.Abs(matrix.m01) * e.y + Mathf.Abs(matrix.m02) * e.z,
                Mathf.Abs(matrix.m10) * e.x + Mathf.Abs(matrix.m11) * e.y + Mathf.Abs(matrix.m12) * e.z,
                Mathf.Abs(matrix.m20) * e.x + Mathf.Abs(matrix.m21) * e.y + Mathf.Abs(matrix.m22) * e.z);
            return new Bounds(matrix.MultiplyPoint3x4(local.center), 2 * (extent + Vector3.one * Mathf.Max(0, padding)));
        }

        // -1 outside, 0 intersects, 1 wholly inside. Plane tests use full bounds, not pivots.
        internal static int Classify(Plane[] planes, Bounds bounds)
        {
            bool inside = true;
            foreach (var plane in planes)
            {
                Vector3 n = plane.normal, e = bounds.extents;
                float r = Mathf.Abs(n.x) * e.x + Mathf.Abs(n.y) * e.y + Mathf.Abs(n.z) * e.z;
                float d = plane.GetDistanceToPoint(bounds.center);
                if (d < -r) return -1;
                if (d < r) inside = false;
            }
            return inside ? 1 : 0;
        }

        internal static bool InRange(Bounds bounds, Vector3 position, float distance)
            => distance <= 0 || bounds.SqrDistance(position) <= distance * distance;

        internal static int SelectLOD(float height, float[] thresholds, int previous)
        {
            int selected = 0;
            while (selected < thresholds.Length && height < thresholds[selected]) ++selected;
            if (previous >= 0 && previous <= thresholds.Length && selected != previous)
            {
                if (selected > previous && height >= thresholds[previous] * 0.9f) return previous;
                if (selected < previous && height <= thresholds[previous - 1] * 1.1f) return previous;
            }
            return selected;
        }
    }
}
