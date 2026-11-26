using System.Collections.Generic;
using UnityEngine;

namespace MimicGame.Level
{
    // Oda temaları, prop'lar, doluluk ızgarası ve ganimet noktaları
    public partial class FacilityGenerator
    {
        // Dekor sırasındaki bağlam
        int curLv;
        float curBase;

        struct Area
        {
            public Space sp;
            public int x0, z0, w, d;
            public float X1 => x0 + w;
            public float Z1 => z0 + d;
            public float Cx => x0 + w * 0.5f;
            public float Cz => z0 + d * 0.5f;
        }

        void AddLoot(Vector3 p) => lootPoints.Add(new Vector3(p.x, p.y + curBase, p.z));

        static float Rf(System.Random rng) => (float)rng.NextDouble();
        static float Rr(System.Random rng, float a, float b) => a + (float)rng.NextDouble() * (b - a);

        // ---------- Doluluk ızgarası (0.5 m) ----------

        bool OccFree(int li, float minX, float minZ, float maxX, float maxZ, bool mark)
        {
            int i0 = Mathf.FloorToInt((minX + Org) * 2f + 0.001f);
            int i1 = Mathf.CeilToInt((maxX + Org) * 2f - 0.001f) - 1;
            int j0 = Mathf.FloorToInt((minZ + Org) * 2f + 0.001f);
            int j1 = Mathf.CeilToInt((maxZ + Org) * 2f - 0.001f) - 1;
            if (i1 < i0) i1 = i0;
            if (j1 < j0) j1 = j0;
            if (i0 < 0 || j0 < 0 || i1 >= OG || j1 >= OG) return false;
            var occ = grids[li].occ;
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    if (occ[j * OG + i]) return false;
            if (mark)
                for (int j = j0; j <= j1; j++)
                    for (int i = i0; i <= i1; i++)
                        occ[j * OG + i] = true;
            return true;
        }

        void OccMark(int li, float minX, float minZ, float maxX, float maxZ)
        {
            int i0 = Mathf.Max(0, Mathf.FloorToInt((minX + Org) * 2f));
            int i1 = Mathf.Min(OG - 1, Mathf.CeilToInt((maxX + Org) * 2f) - 1);
            int j0 = Mathf.Max(0, Mathf.FloorToInt((minZ + Org) * 2f));
            int j1 = Mathf.Min(OG - 1, Mathf.CeilToInt((maxZ + Org) * 2f) - 1);
            for (int j = j0; j <= j1; j++)
                for (int i = i0; i <= i1; i++)
                    grids[li].occ[j * OG + i] = true;
        }

        // Her kapı/geçidin iki yanında 1.5 m boş bölge (prop yerleştirilmez)
        void ReserveDoorZones()
        {
            for (int i = 0; i < doors.Count; i++)
            {
                var d = doors[i];
                float a0 = d.start - 0.2f, a1 = d.start + d.n + 0.2f;
                if (d.alongX) OccMark(d.lv, a0, d.line - 1.5f, a1, d.line + 1.5f);
                else OccMark(d.lv, d.line - 1.5f, a0, d.line + 1.5f, a1);
            }
        }

        bool DoorNear(int li, bool alongX, int line, float along, float margin)
        {
            for (int i = 0; i < doors.Count; i++)
            {
                var d = doors[i];
                if (d.lv != li || d.alongX != alongX || d.line != line) continue;
                if (along >= d.start - margin && along <= d.start + d.n + margin) return true;
            }
            return false;
        }

        // ---------- Duvar boyunca yerleşim ----------

        // side: 0 güney (z=z0), 1 doğu (x=x1), 2 kuzey (z=z1), 3 batı (x=x0)
        static void WallRange(Area a, int side, out float lo, out float hi)
        {
            if (side == 0 || side == 2) { lo = a.x0 + 0.3f; hi = a.X1 - 0.3f; }
            else { lo = a.z0 + 0.3f; hi = a.Z1 - 0.3f; }
        }

        static void WallFrame(Area a, int side, float along, float inset, out Vector3 pos, out float yaw)
        {
            switch (side)
            {
                case 0: pos = new Vector3(along, 0f, a.z0 + 0.1f + inset); yaw = 0f; break;
                case 1: pos = new Vector3(a.X1 - 0.1f - inset, 0f, along); yaw = 270f; break;
                case 2: pos = new Vector3(along, 0f, a.Z1 - 0.1f - inset); yaw = 180f; break;
                default: pos = new Vector3(a.x0 + 0.1f + inset, 0f, along); yaw = 90f; break;
            }
        }

        // Duvar dışında aynı alan yoksa (L biçiminin iç sınırı duvar değildir) true
        bool WallBeyondOk(Area a, int side, float lo, float hi)
        {
            for (float u = lo; u <= hi + 0.001f; u += 0.5f)
            {
                int bx, bz;
                int ui = Mathf.FloorToInt(Mathf.Min(u, hi - 0.01f));
                switch (side)
                {
                    case 0: bx = ui; bz = a.z0 - 1; break;
                    case 1: bx = a.x0 + a.w; bz = ui; break;
                    case 2: bx = ui; bz = a.z0 + a.d; break;
                    default: bx = a.x0 - 1; bz = ui; break;
                }
                if (Owner(curLv, bx, bz) == a.sp.id) return false;
            }
            return true;
        }

        bool TryWallSlot(Area a, int side, float along, float w, float d, float clear, out Vector3 pos, out float yaw)
        {
            WallFrame(a, side, along, 0.02f + d * 0.5f, out pos, out yaw);
            float hw = w * 0.5f;
            if (!WallBeyondOk(a, side, along - hw, along + hw)) return false;
            float depth = 0.02f + d + clear;
            float minX, maxX, minZ, maxZ;
            switch (side)
            {
                case 0: minX = along - hw; maxX = along + hw; minZ = a.z0 + 0.1f; maxZ = minZ + depth; break;
                case 1: maxX = a.X1 - 0.1f; minX = maxX - depth; minZ = along - hw; maxZ = along + hw; break;
                case 2: maxZ = a.Z1 - 0.1f; minZ = maxZ - depth; minX = along - hw; maxX = along + hw; break;
                default: minX = a.x0 + 0.1f; maxX = minX + depth; minZ = along - hw; maxZ = along + hw; break;
            }
            return OccFree(curLv, minX, minZ, maxX, maxZ, true);
        }

        void WallLine(Area a, int side, System.Random rng, float w, float d, float clear, float gapMin, float gapMax,
            double prob, System.Action<Vector3, float> place)
        {
            WallRange(a, side, out float lo, out float hi);
            float u = lo + w * 0.5f;
            int guard = 0;
            while (u + w * 0.5f <= hi + 0.001f && guard++ < 80)
            {
                if (rng.NextDouble() < prob && TryWallSlot(a, side, u, w, d, clear, out Vector3 pos, out float yaw))
                {
                    place(pos, yaw);
                    u += w + gapMin + Rf(rng) * (gapMax - gapMin);
                }
                else u += 0.5f + Rf(rng) * 0.5f;
            }
        }

        // Ortadaki ada: merkez (cx,cz), boyut (sx,sz) dünya ekseninde; duvarlardan en az 'margin' uzak
        bool TryIsland(Area a, float cx, float cz, float sx, float sz, float margin)
        {
            float minX = cx - sx * 0.5f, maxX = cx + sx * 0.5f, minZ = cz - sz * 0.5f, maxZ = cz + sz * 0.5f;
            if (minX < a.x0 + margin || maxX > a.X1 - margin || minZ < a.z0 + margin || maxZ > a.Z1 - margin) return false;
            return OccFree(curLv, minX, minZ, maxX, maxZ, true);
        }

        // ---------- Alan başına dekor ----------

        void Decorate(Space sp, System.Random rng)
        {
            curLv = sp.IsStair ? sp.lvLow : sp.lv;
            curBase = LevelBase[curLv];

            if (sp.kind == RoomKind.Entrance)
            {
                DecorEntrance(sp, new System.Random(1337)); // giriş holü her turda aynı
                return;
            }
            if (sp.IsStair) { DecorStair(sp, rng); return; }
            if (sp.IsCorridor) { DecorCorridor(sp, rng); return; }

            for (int i = 0; i < sp.rects.Count; i++)
            {
                var r = sp.rects[i];
                var a = new Area { sp = sp, x0 = r.x, z0 = r.z, w = r.w, d = r.d };
                bool primary = i == 0;
                DecorRoomArea(sp.kind, a, rng, primary ? sp.density : sp.density * 0.6f, sp.variant, primary);
                Clutter(a, rng, 1 + Mathf.RoundToInt(sp.density * 3f));
                // Zeminde ganimet noktaları (duvar diplerinde)
                int extra = 1 + rng.Next(2);
                for (int k = 0; k < extra; k++)
                {
                    int side = rng.Next(4);
                    WallRange(a, side, out float lo, out float hi);
                    if (hi - lo < 0.6f) continue;
                    float u = Rr(rng, lo + 0.3f, hi - 0.3f);
                    if (TryWallSlot(a, side, u, 0.5f, 0.5f, 0f, out Vector3 pos, out float yaw)) AddLoot(new Vector3(pos.x, 0.05f, pos.z));
                }
            }
        }

        void DecorRoomArea(RoomKind kind, Area a, System.Random rng, float dens, int variant, bool primary)
        {
            switch (kind)
            {
                case RoomKind.Storage: DecorStorage(a, rng, dens, variant, primary); break;
                case RoomKind.Office: DecorOffice(a, rng, dens, variant); break;
                case RoomKind.Bunk: DecorBunk(a, rng, dens, variant); break;
                case RoomKind.Generator: DecorGenerator(a, rng, dens, variant, primary); break;
                case RoomKind.Locker: DecorLocker(a, rng, dens, variant); break;
                case RoomKind.Workshop: DecorWorkshop(a, rng, dens, variant); break;
                case RoomKind.Closet: DecorCloset(a, rng); break;
                case RoomKind.PipeRoom: DecorPipeRoom(a, rng, dens, variant); break;
                case RoomKind.Mess: DecorMess(a, rng, dens, variant); break;
                case RoomKind.Warehouse: DecorWarehouse(a, rng, dens, variant, primary); break;
                case RoomKind.RadioHall: DecorRadioHall(a, rng, dens, primary); break;
                case RoomKind.RadioRoom: DecorRadioRoom(a, rng, dens); break;
                case RoomKind.ControlRoom: DecorControl(a, rng, primary); break;
            }
        }

        // ---------- Giriş holü (sabit) ----------

        void DecorEntrance(Space sp, System.Random rng)
        {
            var r = sp.rects[0];
            var a = new Area { sp = sp, x0 = r.x, z0 = r.z, w = r.w, d = r.d };
            var p = sp.root;
            // Zemin güvenlik çizgileri: çıkış kapısından koridora
            FloorLine(p, new Vector3(3.2f, 0f, 3.8f), new Vector3(0.12f, 0.012f, 6.4f));
            FloorLine(p, new Vector3(5.8f, 0f, 3.8f), new Vector3(0.12f, 0.012f, 6.4f));
            FloorLine(p, new Vector3(4.5f, 0f, 0.9f), new Vector3(2.7f, 0.012f, 0.12f));

            // Batı duvarı: masa + sandalye; doğu duvarı: bankler; köşelerde sandık/varil
            if (TryWallSlot(a, 3, 3.5f, 1.6f, 0.8f, 1.0f, out Vector3 dp, out float dy))
            {
                Table(p, dp, dy, 1.6f, rng, true);
                Chair(p, Local(dp, dy, new Vector3(0f, 0f, 0.95f)), dy + 180f);
            }
            if (TryWallSlot(a, 1, 2.2f, 1.5f, 0.4f, 0.8f, out Vector3 b1, out float by1)) Bench(p, b1, by1, 1.5f);
            if (TryWallSlot(a, 1, 4.6f, 1.5f, 0.4f, 0.8f, out Vector3 b2, out float by2)) Bench(p, b2, by2, 1.5f);
            if (TryWallSlot(a, 3, 1.0f, 0.9f, 0.9f, 0.4f, out Vector3 c1, out float cy1)) Barrel(p, c1, rng);
            if (TryWallSlot(a, 0, 7.8f, 0.8f, 0.8f, 0.4f, out Vector3 c2, out float cy2)) Crate(p, c2, 0.8f, 8f, rng, false);
            if (TryWallSlot(a, 2, 1.0f, 0.5f, 0.6f, 0.6f, out Vector3 f1, out float fy1)) FilingCabinet(p, f1, fy1);
            if (TryWallSlot(a, 2, 8.0f, 0.5f, 0.6f, 0.6f, out Vector3 f2, out float fy2)) FilingCabinet(p, f2, fy2);
            if (TryWallSlot(a, 1, 6.2f, 1.8f, 0.5f, 0.8f, out Vector3 s1, out float sy1)) Shelf(p, s1, sy1, rng);
            HorizontalPipe(p, a, 2.9f, 0.18f, 0.7f);
        }

        // ---------- Koridor ----------

        void DecorCorridor(Space sp, System.Random rng)
        {
            var r = sp.rects[0];
            var p = sp.root;
            bool alongZ = AlongZ(sp);
            CorridorRange(sp, out int cs, out int ce);
            float len = ce - cs;
            float cx = r.x + 1.5f, cz = r.z + 1.5f;
            var a = new Area { sp = sp, x0 = r.x, z0 = r.z, w = r.w, d = r.d };

            // Sarı güvenlik çizgileri (kapı ve kavşak boşluklarında kesilir)
            for (int s = -1; s <= 1; s += 2)
            {
                int wallLine = alongZ ? (s > 0 ? r.x + r.w : r.x) : (s > 0 ? r.z + r.d : r.z);
                var cuts = new List<Vector2>();
                for (int i = 0; i < doors.Count; i++)
                {
                    var d = doors[i];
                    if (d.lv != sp.lv) continue;
                    if (d.alongX == alongZ) continue; // yalnızca yan duvardaki kapılar
                    if (d.line != wallLine) continue;
                    if (d.start + d.n < cs || d.start > ce) continue;
                    cuts.Add(new Vector2(d.start - 0.2f, d.start + d.n + 0.2f));
                }
                cuts.Sort((m1, m2) => m1.x.CompareTo(m2.x));
                float cur = cs + 0.4f;
                for (int i = 0; i <= cuts.Count; i++)
                {
                    float segEnd = i < cuts.Count ? cuts[i].x : ce - 0.4f;
                    if (segEnd - cur > 0.5f)
                    {
                        float mid = (cur + segEnd) * 0.5f, l = segEnd - cur;
                        float lat = s * 1.2f;
                        if (alongZ) FloorLine(p, new Vector3(cx + lat, 0f, mid), new Vector3(0.1f, 0.012f, l));
                        else FloorLine(p, new Vector3(mid, 0f, cz + lat), new Vector3(l, 0.012f, 0.1f));
                    }
                    if (i < cuts.Count) cur = Mathf.Max(cur, cuts[i].y);
                }
            }

            // Tavan boruları ve kablo yolu
            Vector3 lat1 = alongZ ? Vector3.right : Vector3.forward;
            Vector3 startPt = alongZ ? new Vector3(cx, 0f, cs) : new Vector3(cs, 0f, cz);
            Vector3 endPt = alongZ ? new Vector3(cx, 0f, ce) : new Vector3(ce, 0f, cz);
            float h = sp.h;
            if (rng.NextDouble() < 0.8)
                Pipe(p, startPt + lat1 * -1.15f + Vector3.up * (h - 0.28f), endPt + lat1 * -1.15f + Vector3.up * (h - 0.28f), 0.16f);
            if (rng.NextDouble() < 0.6)
                Pipe(p, startPt + lat1 * -0.9f + Vector3.up * (h - 0.2f), endPt + lat1 * -0.9f + Vector3.up * (h - 0.2f), 0.09f);
            if (rng.NextDouble() < 0.6)
            {
                Vector3 mid = (startPt + endPt) * 0.5f + lat1 * 1.0f + Vector3.up * (h - 0.12f);
                Vector3 size = alongZ ? new Vector3(0.4f, 0.05f, len - 0.4f) : new Vector3(len - 0.4f, 0.05f, 0.4f);
                Block(p, "Kablo Yolu", mid, size, metalMat, false);
            }

            // Uyarı levhaları, yırtık tavan paneli, dağınık sandık/varil
            int signs = rng.Next(0, 3);
            for (int i = 0; i < signs; i++)
            {
                int side = alongZ ? (rng.Next(2) == 0 ? 1 : 3) : (rng.Next(2) == 0 ? 0 : 2);
                float u = Rr(rng, cs + 1.5f, ce - 1.5f);
                int wl = SideLine(a, side);
                if (DoorNear(curLv, !(side == 1 || side == 3), wl, u, 1.2f)) continue;
                WallFrame(a, side, u, 0.03f, out Vector3 sp0, out float sy);
                WarnSign(p, new Vector3(sp0.x, 1.5f, sp0.z), sy);
            }
            int loose = rng.Next(0, 3);
            for (int i = 0; i < loose; i++)
            {
                int side = alongZ ? (rng.Next(2) == 0 ? 1 : 3) : (rng.Next(2) == 0 ? 0 : 2);
                float u = Rr(rng, cs + 1f, ce - 1f);
                if (!TryWallSlot(a, side, u, 0.8f, 0.8f, 1.0f, out Vector3 pos, out float yaw)) continue;
                int k = rng.Next(3);
                if (k == 0) Crate(p, pos, 0.7f, Rr(rng, -12f, 12f), rng, true);
                else if (k == 1) Barrel(p, pos, rng);
                else Pallet(p, pos, yaw, rng);
            }
            Clutter(a, rng, 2 + rng.Next(3));
        }

        static int SideLine(Area a, int side)
        {
            switch (side)
            {
                case 0: return a.z0;
                case 1: return a.x0 + a.w;
                case 2: return a.z0 + a.d;
                default: return a.x0;
            }
        }

        // ---------- Merdiven ----------

        void DecorStair(Space sp, System.Random rng)
        {
            var r = sp.rects[0];
            int dx = sp.dirX, dz = sp.dirZ;
            float yaw = Mathf.Atan2(dx, dz) * Mathf.Rad2Deg;
            Vector3 origin;
            if (dx > 0) origin = new Vector3(r.x, 0f, r.z + 1.5f);
            else if (dx < 0) origin = new Vector3(r.x + r.w, 0f, r.z + 1.5f);
            else if (dz > 0) origin = new Vector3(r.x + 1.5f, 0f, r.z);
            else origin = new Vector3(r.x + 1.5f, 0f, r.z + r.d);

            var frame = Prop(sp.root, "Merdiven", origin, yaw);
            float width = 2.8f;
            const int steps = 20;
            const float tread = 0.3f;
            for (int i = 1; i <= steps; i++)
            {
                float top = sp.nearUpper ? StepH - 0.2f * i : 0.2f * i;
                if (top < 0.05f) continue;
                Block(frame, "Basamak", new Vector3(0f, top * 0.5f, 1f + tread * (i - 1) + tread * 0.5f),
                    new Vector3(width, top, tread), concrete);
            }
            // Korkuluklar (görsel): eğimli ince çubuklar
            float ang = Mathf.Atan2(0.2f, tread) * Mathf.Rad2Deg;
            float runLen = steps * tread;
            for (int s = -1; s <= 1; s += 2)
            {
                float yStart = sp.nearUpper ? StepH + 0.9f : 0.9f;
                var rail = Pivot(frame, "Korkuluk", new Vector3(s * 1.33f, yStart, 1f), Quaternion.Euler(sp.nearUpper ? ang : -ang, 0f, 0f));
                Block(rail, "Tutamak", new Vector3(0f, 0f, runLen * 0.5f / Mathf.Cos(ang * Mathf.Deg2Rad)),
                    new Vector3(0.05f, 0.05f, runLen / Mathf.Cos(ang * Mathf.Deg2Rad)), metalMat, false);
            }
            // Üst sahanlık kenarında sarı uyarı şeridi (üst kat yerelinde)
            float zEdge = sp.nearUpper ? 0.92f : 7.08f;
            var up = Prop(sp.rootUp, "Sahanlık", origin, yaw);
            FloorLineLocal(up, new Vector3(0f, 0.006f, zEdge), new Vector3(2.8f, 0.012f, 0.14f));
        }

        // ---------- Temalar ----------

        void DecorStorage(Area a, System.Random rng, float dens, int variant, bool primary)
        {
            var p = a.sp.root;
            if (variant != 1 || !primary)
                for (int s = 0; s < 4; s++)
                    WallLine(a, s, rng, 1.8f, 0.5f, 0.9f, 0.1f, 0.8f, 0.9 * dens, (pos, yaw) => Shelf(p, pos, yaw, rng));
            if (variant >= 1 && primary)
            {
                bool alongX = a.w >= a.d;
                if (alongX)
                {
                    for (float z = a.z0 + 2.4f; z <= a.Z1 - 2.4f; z += 2.8f)
                        for (float x = a.x0 + 2.3f; x <= a.X1 - 2.3f; x += 1.95f)
                            if (rng.NextDouble() < 0.85 * dens + 0.1 && TryIsland(a, x, z, 1.8f, 0.5f, 1.6f)) Shelf(p, new Vector3(x, 0f, z), rng.Next(2) == 0 ? 0f : 180f, rng);
                }
                else
                {
                    for (float x = a.x0 + 2.4f; x <= a.X1 - 2.4f; x += 2.8f)
                        for (float z = a.z0 + 2.3f; z <= a.Z1 - 2.3f; z += 1.95f)
                            if (rng.NextDouble() < 0.85 * dens + 0.1 && TryIsland(a, x, z, 0.5f, 1.8f, 1.6f)) Shelf(p, new Vector3(x, 0f, z), rng.Next(2) == 0 ? 90f : 270f, rng);
                }
            }
            if (variant == 2 || !primary)
                for (int s = 0; s < 4; s++)
                    WallLine(a, s, rng, 1.1f, 1.1f, 0.5f, 0.2f, 1.2f, 0.5 * dens, (pos, yaw) => Pallet(p, pos, yaw, rng));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.8f, 0.8f, 0.4f, 0.4f, 2f, 0.3 * dens, (pos, yaw) =>
                {
                    if (rng.Next(2) == 0) Crate(p, pos, Rr(rng, 0.6f, 0.9f), Rr(rng, -10f, 10f), rng, true);
                    else Barrel(p, pos, rng);
                });
        }

        void DecorOffice(Area a, System.Random rng, float dens, int variant)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.6f, 0.8f, 1.1f, 0.4f, 1.5f, 0.55 * dens + 0.15, (pos, yaw) =>
                {
                    Table(p, pos, yaw, 1.6f, rng, true);
                    if (rng.NextDouble() < 0.8) Chair(p, Local(pos, yaw, new Vector3(Rr(rng, -0.3f, 0.3f), 0f, 0.95f)), yaw + 180f + Rr(rng, -25f, 25f));
                });
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.5f, 0.6f, 0.7f, 0.1f, 1f, 0.5 * dens, (pos, yaw) => FilingCabinet(p, pos, yaw));
            if (variant == 1)
                for (int s = 0; s < 4; s++)
                    WallLine(a, s, rng, 1.8f, 0.5f, 0.9f, 0.2f, 0.8f, 0.4 * dens, (pos, yaw) => Shelf(p, pos, yaw, rng));
            // Ortada toplantı masası
            if (variant == 2 && TryIsland(a, a.Cx, a.Cz, 2.4f, 1.2f, 1.7f))
            {
                Table(p, new Vector3(a.Cx, 0f, a.Cz), 0f, 2.0f, rng, false);
                Chair(p, new Vector3(a.Cx - 0.5f, 0f, a.Cz + 0.9f), 180f);
                Chair(p, new Vector3(a.Cx + 0.5f, 0f, a.Cz - 0.9f), 0f);
            }
            // Dağınık kağıtlar
            for (int i = 0; i < 4 + rng.Next(5); i++)
                Paper(p, new Vector3(Rr(rng, a.x0 + 0.8f, a.X1 - 0.8f), 0f, Rr(rng, a.z0 + 0.8f, a.Z1 - 0.8f)), rng);
        }

        void DecorBunk(Area a, System.Random rng, float dens, int variant)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 2.05f, 0.95f, 0.9f, 0.3f, 1.2f, 0.8 * dens + 0.1, (pos, yaw) =>
                {
                    if (rng.NextDouble() < 0.6) BunkBed(p, pos, yaw, rng);
                    else Cot(p, pos, yaw, rng);
                });
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.26f, 0.5f, 0.8f, 0.2f, 1f, 0.35 * dens, (pos, yaw) => LockerRow(p, pos, yaw, 3));
            if (variant == 1 && TryIsland(a, a.Cx, a.Cz, 1.6f, 0.9f, 1.7f))
            {
                Table(p, new Vector3(a.Cx, 0f, a.Cz), 0f, 1.4f, rng, false);
                Chair(p, new Vector3(a.Cx, 0f, a.Cz + 0.8f), 180f + Rr(rng, -20f, 20f));
            }
        }

        void DecorGenerator(Area a, System.Random rng, float dens, int variant, bool primary)
        {
            var p = a.sp.root;
            bool gAlongX = a.w >= a.d;
            if (primary && TryIsland(a, a.Cx, a.Cz, gAlongX ? 3.4f : 1.8f, gAlongX ? 1.8f : 3.4f, 1.7f))
            {
                GeneratorBlock(p, new Vector3(a.Cx, 0f, a.Cz), gAlongX ? 0f : 90f, rng);
                StripeRect(p, a.Cx, a.Cz, gAlongX ? 4.6f : 3.0f, gAlongX ? 3.0f : 4.6f);
            }
            HorizontalPipe(p, a, 2.5f, 0.2f, 1.1f);
            HorizontalPipe(p, a, 2.9f, 0.14f, 0.8f);
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.8f, 0.8f, 0.5f, 0.3f, 2.5f, 0.45 * dens, (pos, yaw) => Barrel(p, pos, rng));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.0f, 0.5f, 0.8f, 0.5f, 2f, 0.3 * dens, (pos, yaw) => Cabinet(p, pos, yaw));
            if (variant == 1)
                for (int s = 0; s < 4; s++)
                    WallLine(a, s, rng, 2.0f, 1.1f, 0.8f, 0.5f, 2f, 0.4, (pos, yaw) => FuelTank(p, pos, yaw));
        }

        void DecorLocker(Area a, System.Random rng, float dens, int variant)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.68f, 0.5f, 1.0f, 0.1f, 0.6f, 0.9 * dens, (pos, yaw) => LockerRow(p, pos, yaw, 4));
            if (TryIsland(a, a.Cx, a.Cz, a.w >= a.d ? 2.4f : 0.5f, a.w >= a.d ? 0.5f : 2.4f, 1.8f))
                Bench(p, new Vector3(a.Cx, 0f, a.Cz), a.w >= a.d ? 0f : 90f, 2.2f);
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.5f, 0.5f, 0.4f, 0.5f, 2f, 0.3 * dens, (pos, yaw) => Bucket(p, pos, rng));
        }

        void DecorWorkshop(Area a, System.Random rng, float dens, int variant)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.8f, 0.8f, 1.0f, 0.3f, 1.2f, 0.7 * dens + 0.1, (pos, yaw) => Workbench(p, pos, yaw, rng));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.8f, 0.5f, 0.9f, 0.2f, 0.8f, 0.4 * dens, (pos, yaw) => Shelf(p, pos, yaw, rng));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.8f, 0.8f, 0.4f, 0.4f, 2f, 0.4 * dens, (pos, yaw) =>
                {
                    if (rng.Next(2) == 0) Barrel(p, pos, rng); else Crate(p, pos, 0.7f, Rr(rng, -10f, 10f), rng, true);
                });
            if (variant == 2 && TryIsland(a, a.Cx, a.Cz, 1.8f, 0.9f, 1.7f))
                Workbench(p, new Vector3(a.Cx, 0f, a.Cz), 0f, rng);
        }

        void DecorCloset(Area a, System.Random rng)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.2f, 0.5f, 0.6f, 0.1f, 0.4f, 0.8, (pos, yaw) => Shelf(p, pos, yaw, rng, 1.2f));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.5f, 0.5f, 0.3f, 0.2f, 1f, 0.5, (pos, yaw) => Bucket(p, pos, rng));
        }

        void DecorPipeRoom(Area a, System.Random rng, float dens, int variant)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.3f, 1.3f, 0.7f, 0.3f, 1.5f, 0.55 * dens + 0.1, (pos, yaw) => Tank(p, pos, rng));
            HorizontalPipe(p, a, 2.4f, 0.22f, 1.2f);
            HorizontalPipe(p, a, 2.8f, 0.14f, 1.0f);
            HorizontalPipe(p, a, 1.2f, 0.12f, 0.5f);
            // Vanalar
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.4f, 0.3f, 0.5f, 0.5f, 1.5f, 0.5, (pos, yaw) => Valve(p, pos, yaw));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.8f, 0.8f, 0.4f, 0.4f, 2f, 0.35 * dens, (pos, yaw) => Barrel(p, pos, rng));
        }

        void DecorMess(Area a, System.Random rng, float dens, int variant)
        {
            var p = a.sp.root;
            bool alongX = a.w >= a.d;
            // Uzun masa sıraları ve bankler
            if (alongX)
            {
                for (float z = a.z0 + 2.3f; z <= a.Z1 - 2.3f; z += 2.6f)
                    for (float x = a.x0 + 2.3f; x <= a.X1 - 2.3f; x += 2.6f)
                        if (rng.NextDouble() < 0.8 * dens + 0.15 && TryIsland(a, x, z, 2.2f, 1.9f, 1.7f))
                        {
                            Table(p, new Vector3(x, 0f, z), 0f, 2.0f, rng, false);
                            Bench(p, new Vector3(x, 0f, z + 0.8f), 0f, 1.8f);
                            Bench(p, new Vector3(x, 0f, z - 0.8f), 0f, 1.8f);
                        }
            }
            else
            {
                for (float x = a.x0 + 2.3f; x <= a.X1 - 2.3f; x += 2.6f)
                    for (float z = a.z0 + 2.3f; z <= a.Z1 - 2.3f; z += 2.6f)
                        if (rng.NextDouble() < 0.8 * dens + 0.15 && TryIsland(a, x, z, 1.9f, 2.2f, 1.7f))
                        {
                            Table(p, new Vector3(x, 0f, z), 90f, 2.0f, rng, false);
                            Bench(p, new Vector3(x + 0.8f, 0f, z), 90f, 1.8f);
                            Bench(p, new Vector3(x - 0.8f, 0f, z), 90f, 1.8f);
                        }
            }
            // Tezgâh ve raflar
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 2.0f, 0.7f, 0.9f, 0.3f, 1f, 0.4 * dens, (pos, yaw) => Table(p, pos, yaw, 2.0f, rng, true));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.8f, 0.8f, 0.4f, 0.5f, 2f, 0.3 * dens, (pos, yaw) => Barrel(p, pos, rng));
        }

        // Yüksek depo salonu: raf sıraları, palet ve kasa yığınları, kedi yolu
        void DecorWarehouse(Area a, System.Random rng, float dens, int variant, bool primary)
        {
            var p = a.sp.root;
            if (primary) Catwalk(a, rng);
            bool alongX = a.w >= a.d;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.8f, 0.5f, 1.0f, 0.1f, 0.5f, 0.8 * dens + 0.1, (pos, yaw) => Shelf(p, pos, yaw, rng));
            if (alongX)
            {
                for (float z = a.z0 + 3.2f; z <= a.Z1 - 3.2f; z += 3.2f)
                    for (float x = a.x0 + 2.3f; x <= a.X1 - 2.3f; x += 1.95f)
                        if (rng.NextDouble() < 0.75 * dens + 0.1 && TryIsland(a, x, z, 1.8f, 0.5f, 1.6f)) Shelf(p, new Vector3(x, 0f, z), 0f, rng);
            }
            else
            {
                for (float x = a.x0 + 3.2f; x <= a.X1 - 3.2f; x += 3.2f)
                    for (float z = a.z0 + 2.3f; z <= a.Z1 - 2.3f; z += 1.95f)
                        if (rng.NextDouble() < 0.75 * dens + 0.1 && TryIsland(a, x, z, 0.5f, 1.8f, 1.6f)) Shelf(p, new Vector3(x, 0f, z), 90f, rng);
            }
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.1f, 1.1f, 0.6f, 0.4f, 2f, 0.5 * dens, (pos, yaw) => Pallet(p, pos, yaw, rng));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.8f, 0.8f, 0.4f, 0.4f, 2f, 0.35 * dens, (pos, yaw) => Barrel(p, pos, rng));
        }

        // ---------- Telsiz / kontrol (bilgisayar benzeri her şey yalnızca burada) ----------

        void DecorControl(Area a, System.Random rng, bool primary)
        {
            var p = a.sp.root;
            if (!primary) { WallLine(a, 0, rng, 0.9f, 0.7f, 0.8f, 0.3f, 1f, 0.6, (pos, yaw) => Rack(p, pos, yaw, rng, true)); return; }
            // Kuzey duvarında büyük pano
            WallFrame(a, 2, a.Cx, 0.05f, out Vector3 wp, out float wy);
            float pw = Mathf.Min(8f, a.w - 4f);
            WallPanel(p, new Vector3(wp.x, 1.7f, wp.z), wy, pw, 2.8f, rng);
            OccMark(curLv, a.Cx - pw * 0.5f, a.Z1 - 0.9f, a.Cx + pw * 0.5f, a.Z1);
            // Konsol sıraları (yüzleri güneye, operatör güneyde)
            float[] rowZ = { a.Z1 - 3.4f, a.Z1 - 5.8f };
            for (int r = 0; r < rowZ.Length; r++)
            {
                for (float x = a.x0 + 2.4f; x <= a.X1 - 2.4f; x += 2.6f)
                {
                    if (Mathf.Abs(x - a.Cx) < 1.4f) continue;
                    if (!TryIsland(a, x, rowZ[r] - 0.5f, 2.5f, 2.6f, 1.2f)) continue;
                    ConsoleDesk(p, new Vector3(x, 0f, rowZ[r]), 180f, 2.4f, rng, true);
                    Chair(p, new Vector3(x + Rr(rng, -0.3f, 0.3f), 0f, rowZ[r] - 0.95f), Rr(rng, -25f, 25f));
                }
            }
            for (int s = 1; s <= 3; s += 2)
                WallLine(a, s, rng, 2.4f, 0.8f, 1.0f, 0.5f, 2f, 0.6, (pos, yaw) => ConsoleDesk(p, pos, yaw, 2.4f, rng, true));
            WallLine(a, 0, rng, 0.9f, 0.7f, 0.9f, 1f, 3f, 0.4, (pos, yaw) => Rack(p, pos, yaw, rng, true));
        }

        void DecorRadioHall(Area a, System.Random rng, float dens, bool primary)
        {
            var p = a.sp.root;
            if (primary) Catwalk(a, rng);
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.62f, 0.8f, 0.9f, 0.05f, 0.5f, 0.85 * dens + 0.1, (pos, yaw) => Rack(p, pos, yaw, rng, true));
            // Orta raf sıraları
            bool alongX = a.w >= a.d;
            if (primary)
            {
                if (alongX)
                {
                    for (float z = a.z0 + 3.4f; z <= a.Z1 - 3.4f; z += 3.2f)
                        for (float x = a.x0 + 2.6f; x <= a.X1 - 2.6f; x += 0.7f)
                            if (rng.NextDouble() < 0.9 && TryIsland(a, x, z, 0.64f, 0.84f, 1.8f)) Rack(p, new Vector3(x, 0f, z), rng.Next(2) == 0 ? 0f : 180f, rng, rng.NextDouble() < 0.4);
                }
                else
                {
                    for (float x = a.x0 + 3.4f; x <= a.X1 - 3.4f; x += 3.2f)
                        for (float z = a.z0 + 2.6f; z <= a.Z1 - 2.6f; z += 0.7f)
                            if (rng.NextDouble() < 0.9 && TryIsland(a, x, z, 0.84f, 0.64f, 1.8f)) Rack(p, new Vector3(x, 0f, z), rng.Next(2) == 0 ? 90f : 270f, rng, rng.NextDouble() < 0.4);
                }
            }
            // Zemin kabloları
            for (int i = 0; i < 6; i++)
            {
                Vector3 s0 = new Vector3(Rr(rng, a.x0 + 1f, a.X1 - 1f), 0f, Rr(rng, a.z0 + 1f, a.Z1 - 1f));
                Vector3 s1 = s0 + new Vector3(Rr(rng, -3f, 3f), 0f, Rr(rng, -3f, 3f));
                Cable(p, s0, s1);
            }
            // Anten besleme boruları (tavana kadar dikey)
            for (int i = 0; i < 2; i++)
            {
                float x = Rr(rng, a.x0 + 1f, a.X1 - 1f), z = Rr(rng, a.z0 + 1f, a.Z1 - 1f);
                if (!TryIsland(a, x, z, 0.5f, 0.5f, 1.4f)) continue;
                Cyl(p, "Dikey Boru", new Vector3(x, a.sp.h * 0.5f, z), 0.3f, a.sp.h, pipeMat, true);
            }
        }

        void DecorRadioRoom(Area a, System.Random rng, float dens)
        {
            var p = a.sp.root;
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 0.62f, 0.8f, 0.9f, 0.05f, 0.4f, 0.7, (pos, yaw) => Rack(p, pos, yaw, rng, true));
            for (int s = 0; s < 4; s++)
                WallLine(a, s, rng, 1.4f, 0.75f, 1.1f, 0.5f, 1.5f, 0.5, (pos, yaw) =>
                {
                    RadioDesk(p, pos, yaw, rng);
                    Chair(p, Local(pos, yaw, new Vector3(0f, 0f, 0.95f)), yaw + 180f + Rr(rng, -20f, 20f));
                });
            for (int i = 0; i < 4; i++)
            {
                Vector3 s0 = new Vector3(Rr(rng, a.x0 + 1f, a.X1 - 1f), 0f, Rr(rng, a.z0 + 1f, a.Z1 - 1f));
                Cable(p, s0, s0 + new Vector3(Rr(rng, -2.5f, 2.5f), 0f, Rr(rng, -2.5f, 2.5f)));
            }
        }

        // ---------- Çeşitli ortak dekor ----------

        // Duvar dibinde dağınık ufak şeyler: moloz, düşmüş tavan paneli, kağıt, kova
        void Clutter(Area a, System.Random rng, int count)
        {
            var p = a.sp.root;
            for (int i = 0; i < count; i++)
            {
                int side = rng.Next(4);
                WallRange(a, side, out float lo, out float hi);
                if (hi - lo < 1f) continue;
                float u = Rr(rng, lo + 0.3f, hi - 0.3f);
                if (!TryWallSlot(a, side, u, 0.6f, 0.6f, 0f, out Vector3 pos, out float yaw)) continue;
                int k = rng.Next(5);
                if (k == 0)
                {
                    for (int j = 0; j < 3; j++)
                        Block(p, "Moloz", pos + new Vector3(Rr(rng, -0.25f, 0.25f), 0.06f, Rr(rng, -0.25f, 0.25f)),
                            new Vector3(Rr(rng, 0.1f, 0.3f), Rr(rng, 0.06f, 0.18f), Rr(rng, 0.1f, 0.3f)), concrete, false, Rr(rng, 0f, 180f));
                }
                else if (k == 1)
                {
                    var t = Pivot(p, "Düşmüş Panel", pos + new Vector3(0f, 0.35f, 0f), Quaternion.Euler(-65f, yaw, Rr(rng, -8f, 8f)));
                    Block(t, "Panel", Vector3.zero, new Vector3(1.2f, 0.03f, 0.6f), metalMat, false);
                }
                else if (k == 2) Paper(p, pos, rng);
                else if (k == 3) Bucket(p, pos, rng);
                else
                {
                    var t = Pivot(p, "Düşmüş Panel", pos + new Vector3(0.1f, 0.06f, 0f), Quaternion.Euler(Rr(rng, -6f, 6f), Rr(rng, 0f, 360f), Rr(rng, -6f, 6f)));
                    Block(t, "Panel", Vector3.zero, new Vector3(1.2f, 0.03f, 0.6f), metalMat, false);
                }
            }
        }

        void HorizontalPipe(Transform p, Area a, float height, float dia, float inset)
        {
            bool alongX = a.w >= a.d;
            Vector3 s0, s1;
            if (alongX)
            {
                s0 = new Vector3(a.x0 + 0.3f, height, a.Z1 - inset);
                s1 = new Vector3(a.X1 - 0.3f, height, a.Z1 - inset);
            }
            else
            {
                s0 = new Vector3(a.X1 - inset, height, a.z0 + 0.3f);
                s1 = new Vector3(a.X1 - inset, height, a.Z1 - 0.3f);
            }
            Pipe(p, s0, s1, dia);
        }

        void FloorLine(Transform parent, Vector3 center, Vector3 size)
            => Block(parent, "Güvenlik Çizgisi", new Vector3(center.x, size.y * 0.5f + 0.0005f, center.z), size, lineMat, false);

        void FloorLineLocal(Transform parent, Vector3 center, Vector3 size)
            => Block(parent, "Uyarı Şeridi", center, size, lineMat, false);

        // Jeneratör etrafında sarı çerçeve şerit
        void StripeRect(Transform p, float cx, float cz, float sx, float sz)
        {
            FloorLine(p, new Vector3(cx, 0f, cz - sz * 0.5f), new Vector3(sx, 0.012f, 0.14f));
            FloorLine(p, new Vector3(cx, 0f, cz + sz * 0.5f), new Vector3(sx, 0.012f, 0.14f));
            FloorLine(p, new Vector3(cx - sx * 0.5f, 0f, cz), new Vector3(0.14f, 0.012f, sz - 0.14f));
            FloorLine(p, new Vector3(cx + sx * 0.5f, 0f, cz), new Vector3(0.14f, 0.012f, sz - 0.14f));
        }

        // ---------- Kedi yolu (mezzanine) ----------

        void Catwalk(Area a, System.Random rng)
        {
            var p = a.sp.root;
            bool longX = a.w >= a.d;
            int side = longX ? (rng.Next(2) == 0 ? 0 : 2) : (rng.Next(2) == 0 ? 1 : 3);
            WallRange(a, side, out float lo, out float hi);
            if (hi - lo < 8f) return;
            const float stairW = 1.2f, stairLen = 4.5f, walkW = 1.6f;
            bool stairAtLo = rng.Next(2) == 0;
            float uStart = stairAtLo ? lo : hi;
            float uDir = stairAtLo ? 1f : -1f;
            bool wallAlongX = side == 0 || side == 2;
            Vector3 alongDir = wallAlongX ? Vector3.right : Vector3.forward;

            // Alan rezervasyonu: kedi yolunun altına prop konmaz
            float depth = walkW + 0.3f;
            switch (side)
            {
                case 0: OccMark(curLv, lo, a.z0 + 0.1f, hi, a.z0 + 0.1f + depth); break;
                case 2: OccMark(curLv, lo, a.Z1 - 0.1f - depth, hi, a.Z1 - 0.1f); break;
                case 1: OccMark(curLv, a.X1 - 0.1f - depth, lo, a.X1 - 0.1f, hi); break;
                default: OccMark(curLv, a.x0 + 0.1f, lo, a.x0 + 0.1f + depth, hi); break;
            }

            // Merdiven (yerel +z duvar boyunca, uDir yönünde)
            Vector3 origin = WallPoint(a, side, uStart, stairW * 0.5f + 0.05f);
            float runYaw = Mathf.Atan2(alongDir.x * uDir, alongDir.z * uDir) * Mathf.Rad2Deg;
            var st = Prop(p, "Kedi Yolu Merdiveni", origin, runYaw);
            const int steps = 15;
            for (int i = 1; i <= steps; i++)
            {
                float top = 0.2f * i;
                Block(st, "Basamak", new Vector3(0f, top * 0.5f, 0.3f * (i - 1) + 0.15f), new Vector3(stairW, top, 0.3f), metalMat);
            }

            // Yürüyüş yolu
            float pStart = uStart + uDir * stairLen;
            float pEnd = uDir > 0f ? hi : lo;
            float plen = Mathf.Abs(pEnd - pStart);
            if (plen < 2f) return;
            float pMid = (pStart + pEnd) * 0.5f;
            Vector3 pc = WallPoint(a, side, pMid, walkW * 0.5f + 0.05f);
            Vector3 psize = wallAlongX ? new Vector3(plen, 0.1f, walkW) : new Vector3(walkW, 0.1f, plen);
            Block(p, "Kedi Yolu", new Vector3(pc.x, 2.95f, pc.z), psize, metalMat);

            // Destek direkleri ve dış kenar korkuluğu
            float uMin = Mathf.Min(pStart, pEnd), uMax = Mathf.Max(pStart, pEnd);
            for (float q = uMin + 0.3f; q <= uMax - 0.2f; q += 2.0f)
            {
                Vector3 qp = WallPoint(a, side, q, walkW);
                Block(p, "Direk", new Vector3(qp.x, 1.45f, qp.z), new Vector3(0.12f, 2.9f, 0.12f), metalMat, false);
                Block(p, "Korkuluk Direği", new Vector3(qp.x, 3.5f, qp.z), new Vector3(0.06f, 1.0f, 0.06f), metalMat, false);
            }
            Vector3 rc = WallPoint(a, side, pMid, walkW);
            Vector3 rsize = wallAlongX ? new Vector3(plen, 0.05f, 0.05f) : new Vector3(0.05f, 0.05f, plen);
            Block(p, "Korkuluk", new Vector3(rc.x, 4.0f, rc.z), rsize, metalMat, true);
            Block(p, "Korkuluk Orta", new Vector3(rc.x, 3.5f, rc.z), rsize, metalMat, false);
        }

        // Duvar yüzünden 'inset' kadar içeride nokta (y = 0)
        Vector3 WallPoint(Area a, int side, float along, float inset)
        {
            WallFrame(a, side, along, inset, out Vector3 pos, out float yaw);
            return pos;
        }

        // ---------- Prop'lar ----------

        void Crate(Transform parent, Vector3 pos, float size, float yaw, System.Random rng, bool stack)
        {
            float h = size * (0.7f + 0.5f * Rf(rng));
            Block(parent, "Sandık", pos + Vector3.up * (h * 0.5f), new Vector3(size, h, size), crateMat, true, yaw);
            Block(parent, "Çıta", pos + Vector3.up * (h * 0.5f), new Vector3(size + 0.02f, 0.06f, size + 0.02f), deskMat, false, yaw);
            if (stack && rng.NextDouble() < 0.35)
            {
                float s2 = size * 0.75f, h2 = s2 * 0.8f;
                Block(parent, "Sandık Üstü", pos + Vector3.up * (h + h2 * 0.5f), new Vector3(s2, h2, s2), crateMat, true, yaw + Rr(rng, -15f, 15f));
                if (rng.NextDouble() < 0.5) AddLoot(pos + Vector3.up * (h + h2 + 0.03f));
            }
            else if (rng.NextDouble() < 0.5) AddLoot(pos + Vector3.up * (h + 0.03f));
        }

        void Barrel(Transform parent, Vector3 pos, System.Random rng)
        {
            Material m = rng.NextDouble() < 0.5 ? rustMat : metalMat;
            Cyl(parent, "Varil", pos + Vector3.up * 0.45f, 0.6f, 0.9f, m, true);
            Cyl(parent, "Varil Bandı", pos + Vector3.up * 0.25f, 0.63f, 0.05f, blackMat, false);
            Cyl(parent, "Varil Bandı", pos + Vector3.up * 0.65f, 0.63f, 0.05f, blackMat, false);
        }

        void Bucket(Transform parent, Vector3 pos, System.Random rng)
        {
            Cyl(parent, "Kova", pos + Vector3.up * 0.18f, 0.32f, 0.36f, metalMat, false);
            Cyl(parent, "Kova Kenarı", pos + Vector3.up * 0.37f, 0.36f, 0.03f, blackMat, false);
        }

        void Pallet(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            var c = Prop(parent, "Palet", pos, yaw);
            Block(c, "Palet", new Vector3(0f, 0.07f, 0f), new Vector3(1.1f, 0.14f, 1.1f), deskMat);
            int n = rng.Next(1, 5);
            float topY = 0.14f;
            for (int i = 0; i < n; i++)
            {
                float ox = (i % 2 == 0 ? -0.27f : 0.27f), oz = (i / 2 == 0 ? -0.27f : 0.27f);
                float bh = Rr(rng, 0.35f, 0.6f);
                Block(c, "Koli", new Vector3(ox, topY + bh * 0.5f, oz), new Vector3(0.48f, bh, 0.48f), crateMat, true, Rr(rng, -8f, 8f));
                if (rng.NextDouble() < 0.4) AddLoot(Local(pos, yaw, new Vector3(ox, topY + bh + 0.03f, oz)));
            }
        }

        void Shelf(Transform parent, Vector3 pos, float yaw, System.Random rng, float L = 1.8f)
        {
            const float D = 0.5f;
            var s = Prop(parent, "Raf", pos, yaw);
            Block(s, "Yan Sol", new Vector3(-(L * 0.5f - 0.025f), 1.0f, 0f), new Vector3(0.05f, 2.0f, D), metalMat);
            Block(s, "Yan Sağ", new Vector3(L * 0.5f - 0.025f, 1.0f, 0f), new Vector3(0.05f, 2.0f, D), metalMat);
            Block(s, "Arka", new Vector3(0f, 1.0f, -(D * 0.5f - 0.015f)), new Vector3(L, 2.0f, 0.03f), metalMat, false);
            float[] ys = { 0.15f, 0.7f, 1.25f, 1.8f };
            for (int i = 0; i < ys.Length; i++)
            {
                Block(s, "Tabla", new Vector3(0f, ys[i], 0f), new Vector3(L - 0.1f, 0.04f, D), deskMat);
                if (rng.NextDouble() < 0.55)
                {
                    float ox = (Rf(rng) - 0.5f) * (L - 0.7f);
                    AddLoot(Local(pos, yaw, new Vector3(ox, ys[i] + 0.05f, 0.02f)));
                }
                if (rng.NextDouble() < 0.6)
                {
                    float bx = (Rf(rng) - 0.5f) * (L - 0.6f);
                    float bh = Rr(rng, 0.18f, 0.32f);
                    Block(s, "Kutu", new Vector3(bx, ys[i] + 0.02f + bh * 0.5f, 0.02f), new Vector3(Rr(rng, 0.25f, 0.4f), bh, 0.3f), crateMat, false, Rr(rng, -10f, 10f));
                }
            }
        }

        void Table(Transform parent, Vector3 pos, float yaw, float L, System.Random rng, bool drawers)
        {
            var d = Prop(parent, "Masa", pos, yaw);
            Block(d, "Tabla", new Vector3(0f, 0.76f, 0f), new Vector3(L, 0.05f, 0.8f), deskMat);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Block(d, "Ayak", new Vector3(sx * (L * 0.5f - 0.06f), 0.37f, sz * 0.34f), new Vector3(0.06f, 0.74f, 0.06f), metalMat, false);
            if (drawers) Block(d, "Çekmece", new Vector3(L * 0.5f - 0.3f, 0.5f, 0f), new Vector3(0.5f, 0.45f, 0.7f), metalMat, false);
            AddLoot(Local(pos, yaw, new Vector3((Rf(rng) - 0.5f) * (L - 0.6f), 0.8f, 0.05f)));
        }

        void Chair(Transform parent, Vector3 pos, float yaw)
        {
            var c = Prop(parent, "Sandalye", pos, yaw);
            Block(c, "Oturak", new Vector3(0f, 0.46f, 0f), new Vector3(0.46f, 0.06f, 0.46f), fabricMat);
            Block(c, "Arkalık", new Vector3(0f, 0.78f, -0.21f), new Vector3(0.46f, 0.5f, 0.05f), fabricMat, false);
            Block(c, "Gövde", new Vector3(0f, 0.24f, 0f), new Vector3(0.06f, 0.42f, 0.06f), metalMat, false);
            Block(c, "Ayak A", new Vector3(0f, 0.04f, 0f), new Vector3(0.5f, 0.04f, 0.07f), metalMat, false);
            Block(c, "Ayak B", new Vector3(0f, 0.04f, 0f), new Vector3(0.07f, 0.04f, 0.5f), metalMat, false);
        }

        void Bench(Transform parent, Vector3 pos, float yaw, float L)
        {
            var b = Prop(parent, "Bank", pos, yaw);
            Block(b, "Oturak", new Vector3(0f, 0.45f, 0f), new Vector3(L, 0.05f, 0.35f), deskMat);
            Block(b, "Ayak", new Vector3(-(L * 0.5f - 0.1f), 0.22f, 0f), new Vector3(0.05f, 0.44f, 0.3f), metalMat, false);
            Block(b, "Ayak", new Vector3(L * 0.5f - 0.1f, 0.22f, 0f), new Vector3(0.05f, 0.44f, 0.3f), metalMat, false);
        }

        void FilingCabinet(Transform parent, Vector3 pos, float yaw)
        {
            var c = Prop(parent, "Dosya Dolabı", pos, yaw);
            Block(c, "Gövde", new Vector3(0f, 0.65f, 0f), new Vector3(0.45f, 1.3f, 0.6f), metalMat);
            for (int i = 0; i < 3; i++)
            {
                Block(c, "Çekmece Çizgisi", new Vector3(0f, 0.25f + i * 0.4f, 0.301f), new Vector3(0.4f, 0.015f, 0.01f), blackMat, false);
                Block(c, "Kol", new Vector3(0f, 0.38f + i * 0.4f, 0.31f), new Vector3(0.12f, 0.025f, 0.02f), blackMat, false);
            }
        }

        void Cabinet(Transform parent, Vector3 pos, float yaw)
        {
            var c = Prop(parent, "Dolap", pos, yaw);
            Block(c, "Gövde", new Vector3(0f, 0.95f, 0f), new Vector3(0.9f, 1.9f, 0.5f), rustMat);
            Block(c, "Orta Çizgi", new Vector3(0f, 0.95f, 0.251f), new Vector3(0.015f, 1.8f, 0.01f), blackMat, false);
            Block(c, "Kol", new Vector3(0.1f, 1.0f, 0.27f), new Vector3(0.04f, 0.2f, 0.03f), blackMat, false);
        }

        void LockerRow(Transform parent, Vector3 pos, float yaw, int count)
        {
            var c = Prop(parent, "Dolap Sırası", pos, yaw);
            float pitch = 0.42f;
            for (int i = 0; i < count; i++)
            {
                float x = (i - (count - 1) * 0.5f) * pitch;
                Block(c, "Dolap", new Vector3(x, 0.9f, 0f), new Vector3(0.4f, 1.8f, 0.5f), rustMat);
                Block(c, "Havalık", new Vector3(x, 1.6f, 0.251f), new Vector3(0.28f, 0.08f, 0.01f), blackMat, false);
                Block(c, "Kol", new Vector3(x + 0.12f, 1.0f, 0.26f), new Vector3(0.03f, 0.12f, 0.02f), blackMat, false);
            }
        }

        void BunkBed(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            var b = Prop(parent, "Ranza", pos, yaw);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Block(b, "Direk", new Vector3(sx * 0.97f, 0.9f, sz * 0.42f), new Vector3(0.06f, 1.8f, 0.06f), metalMat, false);
            Block(b, "Alt Çerçeve", new Vector3(0f, 0.35f, 0f), new Vector3(2.0f, 0.05f, 0.9f), metalMat);
            Block(b, "Üst Çerçeve", new Vector3(0f, 1.25f, 0f), new Vector3(2.0f, 0.05f, 0.9f), metalMat);
            Block(b, "Alt Şilte", new Vector3(0f, 0.43f, 0f), new Vector3(1.9f, 0.1f, 0.82f), fabricMat, false);
            Block(b, "Üst Şilte", new Vector3(0f, 1.33f, 0f), new Vector3(1.9f, 0.1f, 0.82f), fabricMat, false);
            Block(b, "Yastık", new Vector3(-0.8f, 0.52f, 0f), new Vector3(0.35f, 0.1f, 0.5f), paperMat, false);
            Block(b, "Merdiven", new Vector3(1.0f, 0.8f, 0.46f), new Vector3(0.05f, 1.2f, 0.04f), metalMat, false);
            if (rng.NextDouble() < 0.4) AddLoot(Local(pos, yaw, new Vector3(0.3f, 1.42f, 0f)));
        }

        void Cot(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            var b = Prop(parent, "Portatif Yatak", pos, yaw);
            Block(b, "Çerçeve", new Vector3(0f, 0.3f, 0f), new Vector3(2.0f, 0.06f, 0.8f), metalMat);
            Block(b, "Şilte", new Vector3(0f, 0.37f, 0f), new Vector3(1.9f, 0.08f, 0.72f), fabricMat, false);
            Block(b, "Ayak", new Vector3(-0.9f, 0.15f, 0f), new Vector3(0.05f, 0.3f, 0.7f), metalMat, false);
            Block(b, "Ayak", new Vector3(0.9f, 0.15f, 0f), new Vector3(0.05f, 0.3f, 0.7f), metalMat, false);
            if (rng.NextDouble() < 0.3) AddLoot(Local(pos, yaw, new Vector3(0.2f, 0.45f, 0f)));
        }

        void Workbench(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            var w = Prop(parent, "Tezgâh", pos, yaw);
            Block(w, "Tabla", new Vector3(0f, 0.9f, 0f), new Vector3(1.8f, 0.07f, 0.75f), deskMat);
            Block(w, "Alt Raf", new Vector3(0f, 0.25f, 0f), new Vector3(1.7f, 0.04f, 0.7f), metalMat, false);
            for (int sx = -1; sx <= 1; sx += 2)
                for (int sz = -1; sz <= 1; sz += 2)
                    Block(w, "Ayak", new Vector3(sx * 0.85f, 0.45f, sz * 0.33f), new Vector3(0.07f, 0.9f, 0.07f), metalMat, false);
            Block(w, "Mengene", new Vector3(-0.6f, 1.0f, 0.1f), new Vector3(0.2f, 0.14f, 0.15f), blackMat, false);
            Block(w, "Alet", new Vector3(0.2f, 0.945f, 0.05f), new Vector3(0.35f, 0.025f, 0.06f), metalMat, false, Rr(rng, -30f, 30f));
            Block(w, "Pano", new Vector3(0f, 1.6f, -0.36f), new Vector3(1.6f, 0.8f, 0.03f), deskMat, false);
            for (int i = 0; i < 4; i++)
                Block(w, "Alet Gölgesi", new Vector3(-0.6f + i * 0.4f, 1.6f, -0.34f), new Vector3(0.06f, 0.4f, 0.02f), blackMat, false);
            AddLoot(Local(pos, yaw, new Vector3(0.5f, 0.95f, 0.1f)));
        }

        void Tank(Transform parent, Vector3 pos, System.Random rng)
        {
            Cyl(parent, "Tank", pos + Vector3.up * 1.1f, 1.2f, 2.2f, metalMat, true);
            Cyl(parent, "Tank Bandı", pos + Vector3.up * 0.5f, 1.24f, 0.08f, blackMat, false);
            Cyl(parent, "Tank Bandı", pos + Vector3.up * 1.7f, 1.24f, 0.08f, blackMat, false);
            Pipe(parent, pos + Vector3.up * 2.2f, pos + new Vector3(0f, 2.8f, 0f), 0.14f);
        }

        void Valve(Transform parent, Vector3 pos, float yaw)
        {
            var v = Prop(parent, "Vana", new Vector3(pos.x, 1.1f, pos.z), yaw);
            Block(v, "Gövde", Vector3.zero, new Vector3(0.18f, 0.18f, 0.18f), rustMat, false);
            Block(v, "Çark", new Vector3(0f, 0.15f, 0f), new Vector3(0.3f, 0.03f, 0.03f), blackMat, false);
        }

        void FuelTank(Transform parent, Vector3 pos, float yaw)
        {
            var t = Prop(parent, "Yakıt Deposu", pos, yaw);
            var lying = Pivot(t, "Yatay Silindir", new Vector3(0f, 0.6f, 0f), Quaternion.Euler(0f, 0f, 90f));
            var go = new GameObject("Depo");
            go.transform.SetParent(lying, false);
            go.AddComponent<MeshFilter>().sharedMesh = CylinderMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = rustMat;
            Block(t, "Ayak", new Vector3(-0.6f, 0.08f, 0f), new Vector3(0.1f, 0.16f, 0.9f), metalMat, false);
            Block(t, "Ayak", new Vector3(0.6f, 0.08f, 0f), new Vector3(0.1f, 0.16f, 0.9f), metalMat, false);
            var col = new GameObject("Çarpıştırıcı");
            col.transform.SetParent(t, false);
            col.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            col.AddComponent<BoxCollider>().size = new Vector3(2.0f, 1.0f, 1.0f);
        }

        void GeneratorBlock(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            var g = Prop(parent, "Jeneratör", pos, yaw);
            Block(g, "Gövde", new Vector3(0f, 0.65f, 0f), new Vector3(3.0f, 1.1f, 1.3f), rustMat);
            Block(g, "Üst Kapak", new Vector3(0f, 1.25f, 0f), new Vector3(2.8f, 0.1f, 1.1f), metalMat, false);
            Block(g, "Taban", new Vector3(0f, 0.05f, 0f), new Vector3(3.2f, 0.1f, 1.5f), blackMat, false);
            for (int i = 0; i < 6; i++)
                Block(g, "Havalandırma", new Vector3(-1.2f + i * 0.45f, 0.7f, 0.66f), new Vector3(0.3f, 0.5f, 0.02f), blackMat, false);
            Cyl(g, "Egzoz", new Vector3(1.0f, 1.9f, -0.3f), 0.3f, 1.4f, metalMat, false);
            Block(g, "Kontrol Paneli", new Vector3(-1.3f, 1.4f, 0.6f), new Vector3(0.5f, 0.35f, 0.15f), metalMat, false);
            Block(g, "Düğme", new Vector3(-1.4f, 1.45f, 0.69f), new Vector3(0.06f, 0.06f, 0.02f), blackMat, false);
            AddLoot(Local(pos, yaw, new Vector3(0f, 1.32f, 0.2f)));
        }

        void RadioDesk(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            Table(parent, pos, yaw, 1.4f, rng, false);
            var d = Prop(parent, "Telsiz", pos, yaw);
            Block(d, "Telsiz Gövde", new Vector3(-0.2f, 0.92f, 0f), new Vector3(0.45f, 0.22f, 0.28f), fabricMat, false);
            Block(d, "Kadran", new Vector3(-0.3f, 0.95f, 0.145f), new Vector3(0.12f, 0.08f, 0.01f), ledAmberMat, false);
            Block(d, "Anten", new Vector3(0.0f, 1.2f, -0.05f), new Vector3(0.015f, 0.6f, 0.015f), metalMat, false);
        }

        // ---------- Bilgisayar benzeri prop'lar (yalnızca kontrol/telsiz odaları) ----------

        Material LedMat(System.Random rng)
        {
            double q = rng.NextDouble();
            return q < 0.5 ? ledGreenMat : (q < 0.75 ? ledRedMat : ledAmberMat);
        }

        void ConsoleDesk(Transform parent, Vector3 pos, float yaw, float L, System.Random rng, bool loot)
        {
            var c = Prop(parent, "Konsol", pos, yaw);
            Block(c, "Gövde", new Vector3(0f, 0.36f, -0.05f), new Vector3(L, 0.72f, 0.7f), metalMat);
            Block(c, "Tabla", new Vector3(0f, 0.745f, 0f), new Vector3(L + 0.04f, 0.05f, 0.9f), deskMat);
            var tilt = Pivot(c, "Eğik Panel", new Vector3(0f, 1.0f, -0.2f), Quaternion.Euler(55f, 0f, 0f));
            Block(tilt, "Panel", Vector3.zero, new Vector3(L - 0.2f, 0.04f, 0.5f), metalMat, false);
            int n = Mathf.Max(1, (int)((L - 0.2f) / 0.7f));
            for (int i = 0; i < n; i++)
            {
                float x = (i - (n - 1) * 0.5f) * 0.7f;
                Block(tilt, "Ekran", new Vector3(x, 0.025f, 0f), new Vector3(0.55f, 0.01f, 0.34f),
                    rng.NextDouble() < 0.7 ? screenMat : screenAmberMat, false);
            }
            for (int i = 0; i < (int)(L / 0.3f); i++)
                Block(c, "Gösterge", new Vector3(-L * 0.5f + 0.2f + i * 0.3f, 0.785f, 0.3f), new Vector3(0.06f, 0.02f, 0.06f), LedMat(rng), false);
            if (loot && rng.NextDouble() < 0.4) AddLoot(Local(pos, yaw, new Vector3(Rr(rng, -L * 0.4f, L * 0.4f), 0.78f, 0.3f)));
        }

        void WallPanel(Transform parent, Vector3 pos, float yaw, float w, float h, System.Random rng)
        {
            var p = Prop(parent, "Büyük Pano", pos, yaw);
            Block(p, "Pano", new Vector3(0f, h * 0.5f, 0.06f), new Vector3(w, h, 0.12f), doorFrameMat, false);
            int cols = Mathf.Max(2, (int)(w / 0.55f)), rows = Mathf.Max(2, (int)(h / 0.45f));
            for (int j = 0; j < rows; j++)
                for (int i = 0; i < cols; i++)
                {
                    if (rng.NextDouble() < 0.4) continue;
                    double q = rng.NextDouble();
                    Material m = q < 0.5 ? screenMat : (q < 0.8 ? screenAmberMat : ledRedMat);
                    Block(p, "Pano Hücresi", new Vector3((i - (cols - 1) * 0.5f) * 0.52f, 0.25f + j * 0.45f, 0.125f), new Vector3(0.42f, 0.3f, 0.02f), m, false);
                }
        }

        void Rack(Transform parent, Vector3 pos, float yaw, System.Random rng, bool blink)
        {
            var r = Prop(parent, "Raf Dolabı", pos, yaw);
            Block(r, "Gövde", new Vector3(0f, 1.0f, 0f), new Vector3(0.62f, 2.0f, 0.8f), rustMat);
            for (int i = 0; i < 6; i++)
            {
                float y = 0.3f + i * 0.28f;
                Block(r, "Ünite", new Vector3(0f, y, 0.405f), new Vector3(0.52f, 0.2f, 0.02f), doorFrameMat, false);
                Block(r, "LED", new Vector3(-0.18f, y, 0.42f), new Vector3(0.03f, 0.03f, 0.02f), LedMat(rng), false);
                Block(r, "LED", new Vector3(-0.1f, y, 0.42f), new Vector3(0.03f, 0.03f, 0.02f), LedMat(rng), false);
            }
            if (blink) AddBlink(r, new Vector3(0.2f, 1.85f, 0.43f), rng);
        }

        void AddBlink(Transform parent, Vector3 localPos, System.Random rng)
        {
            var go = new GameObject("Yanıp Sönen");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPos;
            go.transform.localScale = Vector3.one * 0.05f;
            go.AddComponent<MeshFilter>().sharedMesh = CubeMesh();
            var rend = go.AddComponent<MeshRenderer>();
            rend.sharedMaterial = rng.NextDouble() < 0.5 ? ledRedMat : ledGreenMat;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = rend.sharedMaterial == ledRedMat ? new Color(1f, 0.15f, 0.1f) : new Color(0.2f, 1f, 0.3f);
            l.intensity = 0.4f;
            l.range = 2.5f;
            l.shadows = LightShadows.None;
            var b = go.AddComponent<MimicGame.Environment.BlinkLight>();
            b.period = Rr(rng, 0.8f, 2f);
            b.onFraction = Rr(rng, 0.2f, 0.5f);
            b.lens = rend;
            if (culler != null) culler.Register(l);
        }

        void Cable(Transform parent, Vector3 a, Vector3 b)
        {
            Vector3 d = b - a;
            d.y = 0f;
            float len = d.magnitude;
            if (len < 0.2f) return;
            float yaw = Mathf.Atan2(-d.z, d.x) * Mathf.Rad2Deg;
            Block(parent, "Kablo", new Vector3((a.x + b.x) * 0.5f, 0.012f, (a.z + b.z) * 0.5f), new Vector3(len, 0.024f, 0.04f), cableMat, false, yaw);
        }

        void WarnSign(Transform parent, Vector3 pos, float yaw)
        {
            var s = Prop(parent, "Uyarı Levhası", pos, yaw);
            Block(s, "Levha", Vector3.zero, new Vector3(0.45f, 0.45f, 0.02f), lineMat, false);
            var st = Pivot(s, "Şeritler", new Vector3(0f, 0f, 0.012f), Quaternion.Euler(0f, 0f, 45f));
            for (int i = -1; i <= 1; i++)
                Block(st, "Şerit", new Vector3(i * 0.16f, 0f, 0f), new Vector3(0.07f, 0.34f, 0.01f), blackMat, false);
        }

        void Paper(Transform parent, Vector3 pos, System.Random rng)
        {
            Block(parent, "Kağıt", new Vector3(pos.x, 0.004f, pos.z), new Vector3(0.21f, 0.004f, 0.3f), paperMat, false, Rr(rng, 0f, 180f));
        }
    }
}
