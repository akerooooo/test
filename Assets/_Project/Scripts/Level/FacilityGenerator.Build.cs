using System.Collections.Generic;
using MimicGame.Environment;
using Unity.AI.Navigation;
using UnityEngine;

namespace MimicGame.Level
{
    // Kabuk (zemin, tavan, duvar, kapı), ışıklar, ilkel şekil yardımcıları ve malzemeler
    public partial class FacilityGenerator
    {
        const int PaintCount = 5;

        Material[] paintLower;
        Material[] paintUpper;
        Material[] floorMats;

        static Mesh cubeMesh, cylinderMesh;

        // ---------- Zemin ve tavan ----------

        void BuildAllSlabs()
        {
            for (int i = 0; i < spaces.Count; i++)
            {
                var sp = spaces[i];
                if (sp.IsStair)
                {
                    BuildSlabs(sp, sp.lvLow, sp.root, true);
                    BuildSlabs(sp, sp.lv, sp.rootUp, false);
                }
                else BuildSlabs(sp, sp.lv, sp.root, true);
            }
        }

        // Stair için zemin/tavan koşulları: alt katta tüm zemin, tavan yok; üst katta yalnızca sahanlık zemini, tüm tavan
        bool FloorCell(Space sp, int li, int x, int z)
        {
            if (!sp.IsStair) return true;
            if (li == sp.lvLow) return true;
            int k = StairK(sp, x, z);
            return sp.nearUpper ? k == 0 : k == 7;
        }

        bool CeilCell(Space sp, int li) => !sp.IsStair || li == sp.lv;

        static int StairK(Space sp, int x, int z)
        {
            var r = sp.rects[0];
            if (sp.dirX != 0) return sp.dirX > 0 ? x - r.x : r.x + r.w - 1 - x;
            return sp.dirZ > 0 ? z - r.z : r.z + r.d - 1 - z;
        }

        void BuildSlabs(Space sp, int li, Transform parent, bool lowerPart)
        {
            Material floorMat = floorMats[sp.floorVar % floorMats.Length];
            for (int ri = 0; ri < sp.rects.Count; ri++)
            {
                var r = sp.rects[ri];
                var floors = MergeCells(r, (x, z) => FloorCell(sp, li, x, z));
                for (int i = 0; i < floors.Count; i++)
                {
                    var q = floors[i];
                    Block(parent, "Zemin", new Vector3(q.x + q.w * 0.5f, -0.1f, q.z + q.d * 0.5f), new Vector3(q.w, 0.2f, q.d), floorMat);
                }
                if (!CeilCell(sp, li)) continue;
                float h = sp.IsStair ? RoomH : sp.h;
                var ceils = MergeCells(r, (x, z) => true);
                for (int i = 0; i < ceils.Count; i++)
                {
                    var q = ceils[i];
                    var c = Block(parent, "Tavan", new Vector3(q.x + q.w * 0.5f, h + SlabT * 0.5f, q.z + q.d * 0.5f),
                        new Vector3(q.w, SlabT, q.d), ceilingMat);
                    c.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // tavanın üstü yürünebilir sayılmasın
                }
            }
        }

        // Hücre kümesini açgözlü dikdörtgenlere böler
        static List<RectI> MergeCells(RectI r, System.Func<int, int, bool> pred)
        {
            var res = new List<RectI>();
            var used = new bool[r.w * r.d];
            for (int j = 0; j < r.d; j++)
                for (int i = 0; i < r.w; i++)
                {
                    if (used[j * r.w + i] || !pred(r.x + i, r.z + j)) continue;
                    int w = 1;
                    while (i + w < r.w && !used[j * r.w + i + w] && pred(r.x + i + w, r.z + j)) w++;
                    int d = 1;
                    bool ok = true;
                    while (j + d < r.d && ok)
                    {
                        for (int k = 0; k < w; k++)
                            if (used[(j + d) * r.w + i + k] || !pred(r.x + i + k, r.z + j + d)) { ok = false; break; }
                        if (ok) d++;
                    }
                    for (int jj = 0; jj < d; jj++)
                        for (int ii = 0; ii < w; ii++) used[(j + jj) * r.w + i + ii] = true;
                    res.Add(new RectI(r.x + i, r.z + j, w, d));
                }
            return res;
        }

        // ---------- Duvarlar ----------

        bool HW(int li, int x, int z) => Owner(li, x, z - 1) != Owner(li, x, z) && HT(li, x, z) != 2;
        bool VW(int li, int x, int z) => Owner(li, x - 1, z) != Owner(li, x, z) && VT(li, x, z) != 2;

        bool EdgeWall(int li, bool alongX, int a, int line) => alongX ? HW(li, a, line) : VW(li, line, a);

        bool Perp(int li, bool alongX, int pos, int line, bool plus)
        {
            if (alongX) return plus ? VW(li, pos, line) : VW(li, pos, line - 1);
            return plus ? HW(li, line, pos) : HW(li, line - 1, pos);
        }

        // Kenarın iki yanındaki alanlar
        void EdgeOwners(int li, bool alongX, int a, int line, out int o1, out int o2)
        {
            if (alongX) { o1 = Owner(li, a, line - 1); o2 = Owner(li, a, line); }
            else { o1 = Owner(li, line - 1, a); o2 = Owner(li, line, a); }
        }

        void EdgeInfo(int li, int o1, int o2, out float hmax, out float top, out int paint)
        {
            hmax = Mathf.Max(HeightAt(li, o1), HeightAt(li, o2));
            top = hmax + ((IsStairLow(li, o1) || IsStairLow(li, o2)) ? 0f : SlabT);
            if (o1 > 0 && o2 > 0) paint = spaces[Mathf.Min(o1, o2) - 1].paint;
            else paint = spaces[Mathf.Max(o1, o2) - 1].paint;
        }

        bool BuildEdge(int li, bool alongX, int a, int line)
        {
            int o1, o2;
            EdgeOwners(li, alongX, a, line, out o1, out o2);
            if (o1 == o2) return false;
            byte t = alongX ? HT(li, a, line) : VT(li, line, a);
            return t == 0;
        }

        void BuildLevelWalls(int li)
        {
            int lo = -Org + 2, hi = G - Org - 2;
            for (int axis = 0; axis < 2; axis++)
            {
                bool alongX = axis == 0;
                for (int line = lo; line < hi; line++)
                {
                    int a = lo;
                    while (a < hi)
                    {
                        if (!BuildEdge(li, alongX, a, line)) { a++; continue; }
                        EdgeOwners(li, alongX, a, line, out int p1, out int p2);
                        EdgeInfo(li, p1, p2, out float h0, out float top0, out int paint0);
                        int b = a + 1;
                        while (b < hi && BuildEdge(li, alongX, b, line))
                        {
                            EdgeOwners(li, alongX, b, line, out int q1, out int q2);
                            EdgeInfo(li, q1, q2, out float h1, out float top1, out int paint1);
                            if (!Mathf.Approximately(h0, h1) || !Mathf.Approximately(top0, top1) || paint0 != paint1) break;
                            b++;
                        }
                        BuildRun(li, alongX, line, a, b, h0, top0, paint0);
                        a = b;
                    }
                }
            }
            for (int i = 0; i < doors.Count; i++)
                if (doors[i].lv == li) BuildDoorOpening(doors[i]);
        }

        // Köşe birleşimleri: X çizgileri köşede 0.1 uzar, Z çizgileri 0.1 kısalır (üst üste binme ve z-fight yok)
        void BuildRun(int li, bool alongX, int line, int a, int b, float h, float top, int paint)
        {
            float s = a, e = b;
            if (!EdgeWall(li, alongX, a - 1, line))
            {
                bool through = Perp(li, alongX, a, line, true) && Perp(li, alongX, a, line, false);
                s = through ? a : (alongX ? a - WallT * 0.5f : a + WallT * 0.5f);
            }
            if (!EdgeWall(li, alongX, b, line))
            {
                bool through = Perp(li, alongX, b, line, true) && Perp(li, alongX, b, line, false);
                e = through ? b : (alongX ? b + WallT * 0.5f : b - WallT * 0.5f);
            }
            WallPiece(li, alongX, line, s, e, 0f, top, h, paint);
        }

        // Duvar parçası: alt yarı boyalı, üst yarı kirli beyaz (iki yığılmış kutu)
        void WallPiece(int li, bool alongX, float line, float a0, float a1, float y0, float y1, float h, int paint)
        {
            if (a1 - a0 < 0.01f || y1 - y0 < 0.01f) return;
            float lowerH = h * 0.45f;
            if (y0 < lowerH - 0.001f)
                WallSeg(li, alongX, line, a0, a1, y0, Mathf.Min(y1, lowerH), paintLower[paint]);
            if (y1 > lowerH + 0.001f)
                WallSeg(li, alongX, line, a0, a1, Mathf.Max(y0, lowerH), y1, paintUpper[paint]);
        }

        void WallSeg(int li, bool alongX, float line, float a0, float a1, float ya, float yb, Material mat)
        {
            float c = (a0 + a1) * 0.5f, len = a1 - a0, hh = yb - ya, my = (ya + yb) * 0.5f;
            Vector3 pos = alongX ? new Vector3(c, my, line) : new Vector3(line, my, c);
            Vector3 size = alongX ? new Vector3(len, hh, WallT) : new Vector3(WallT, hh, len);
            Block(wallRoots[li], "Duvar", pos, size, mat);
        }

        // Kapı / açık geçit: yan dikmeler + lento (kenarlar kare ızgara noktalarında biter)
        void BuildDoorOpening(DoorRec d)
        {
            int li = d.lv;
            float ha = HeightAt(li, d.a), hb = HeightAt(li, d.b);
            float hmax = Mathf.Max(ha, hb);
            float top = hmax + ((IsStairLow(li, d.a) || IsStairLow(li, d.b)) ? 0f : SlabT);
            int paint = (d.a > 0 && d.b > 0) ? spaces[Mathf.Min(d.a, d.b) - 1].paint : spaces[Mathf.Max(d.a, d.b) - 1].paint;
            float a0 = d.start, a1 = d.start + d.n;

            if (d.kind == 2)
            {
                float openH = (ha > 0f && hb > 0f) ? Mathf.Min(ha, hb) : hmax;
                if (top - openH > 0.01f) WallPiece(li, d.alongX, d.line, a0, a1, openH, top, hmax, paint);
                return;
            }

            float j = (d.n - d.width) * 0.5f;
            WallPiece(li, d.alongX, d.line, a0, a0 + j, 0f, top, hmax, paint);
            WallPiece(li, d.alongX, d.line, a1 - j, a1, 0f, top, hmax, paint);
            WallPiece(li, d.alongX, d.line, a0 + j, a1 - j, d.height, top, hmax, paint);
            if (d.kind == 1) DoorFrame(d);
        }

        // Kapı çerçevesi: iç yüzler duvar dikmesinin 1.5 cm içinde (z-fight yok), yalnızca görsel
        void DoorFrame(DoorRec d)
        {
            float w = d.width, H = d.height;
            float mid = d.start + d.n * 0.5f;
            float depth = WallT + 0.06f;
            float px = w * 0.5f + 0.025f;
            float postH = H - 0.015f;
            Transform p = wallRoots[d.lv];
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 pos = d.alongX ? new Vector3(mid + s * px, postH * 0.5f, d.line) : new Vector3(d.line, postH * 0.5f, mid + s * px);
                Vector3 size = d.alongX ? new Vector3(0.08f, postH, depth) : new Vector3(depth, postH, 0.08f);
                Block(p, "Çerçeve", pos, size, doorFrameMat, false);
            }
            Vector3 hp = d.alongX ? new Vector3(mid, H + 0.025f, d.line) : new Vector3(d.line, H + 0.025f, mid);
            Vector3 hs = d.alongX ? new Vector3(w + 0.13f, 0.08f, depth) : new Vector3(depth, 0.08f, w + 0.13f);
            Block(p, "Çerçeve Üst", hp, hs, doorFrameMat, false);
        }

        // Giriş holündeki çıkış kapısı: dış cephedekiyle aynı kit (aynı ölçü ve malzeme)
        void BuildExitDoor()
        {
            var mats = new FacilityDoorMaterials { panel = doorMat, frame = doorFrameMat, metal = metalMat, lamp = exitSignMat };
            FacilityDoorKit.Build(levelRoots[EntLv], "Çıkış Kapısı", new Vector3(4.5f, 0f, 0f), 0f, mats, true, 0.2f);
        }

        // ---------- Işıklar ----------

        void AddSpaceLights(Space sp, System.Random rng)
        {
            if (sp.IsStair) return;
            float h = sp.h;

            if (sp.kind == RoomKind.Entrance)
            {
                // Giriş holü: her turda aynı, düzgün aydınlık
                var fr = new System.Random(7);
                Vector3[] ps = { new Vector3(2.5f, 0f, 3.5f), new Vector3(6.5f, 0f, 3.5f), new Vector3(4.5f, 0f, 5.2f) };
                for (int i = 0; i < ps.Length; i++)
                    Fixture(sp, new Vector3(ps[i].x, h, ps[i].z), 0f, true, 1.2f, 8f, false, i == 0, fr, false);
                return;
            }

            if (sp.IsCorridor)
            {
                CorridorRange(sp, out int cs, out int ce);
                bool alongZ = AlongZ(sp);
                var r = sp.rects[0];
                for (float u = cs + 3f; u < ce - 1f; u += 12f)
                {
                    bool working = rng.NextDouble() < 0.6;
                    bool flicker = working && rng.NextDouble() < 0.3;
                    Vector3 pos = alongZ ? new Vector3(r.x + 1.5f, h, u) : new Vector3(u, h, r.z + 1.5f);
                    Fixture(sp, pos, alongZ ? 90f : 0f, working, 0.9f, 6.5f, flicker, false, rng, true);
                }
                return;
            }

            // Odalar: yalnızca ~%40'ında çalışan ışık; çoğunda tek armatür
            if (!sp.lit)
            {
                if (rng.NextDouble() < 0.35) // sönük armatür görseli
                {
                    var r0 = sp.rects[0];
                    Fixture(sp, new Vector3(r0.x + r0.w * 0.5f, h, r0.z + r0.d * 0.5f), 0f, false, 0.9f, 6.5f, false, false, rng, false);
                }
                return;
            }
            var rr = sp.rects[0];
            bool tall = h >= HallH - 0.01f;
            int n = tall ? rng.Next(1, 3) : (rng.NextDouble() < 0.7 ? 1 : (rng.NextDouble() < 0.8 ? 2 : 3));
            bool longX = rr.w >= rr.d;
            for (int i = 0; i < n; i++)
            {
                float f = (i + 1f) / (n + 1f);
                Vector3 pos = longX
                    ? new Vector3(rr.x + rr.w * f, h, rr.z + rr.d * 0.5f)
                    : new Vector3(rr.x + rr.w * 0.5f, h, rr.z + rr.d * f);
                bool working = rng.NextDouble() < 0.8;
                bool flicker = working && rng.NextDouble() < 0.25;
                float inten = tall ? 1.3f : 0.9f;
                float range = tall ? 9f : 6.5f;
                Fixture(sp, pos, longX ? 0f : 90f, working, inten, range, flicker, false, rng, false);
            }
        }

        // Tavan armatürü: gövde + tüp; çalışıyorsa nokta ışık
        void Fixture(Space sp, Vector3 pos, float yaw, bool working, float intensity, float range, bool flicker,
            bool shadows, System.Random rng, bool corridor)
        {
            Transform p = sp.root;
            Block(p, "Armatür", new Vector3(pos.x, pos.y - 0.045f, pos.z), new Vector3(1.3f, 0.07f, 0.28f), metalMat, false, yaw);
            Block(p, "Tüp", new Vector3(pos.x, pos.y - 0.1f, pos.z), new Vector3(1.2f, 0.03f, 0.14f),
                working ? stripOnMat : stripOffMat, false, yaw);
            if (!working) return;
            var go = new GameObject("Floresan Işık");
            go.transform.SetParent(p, false);
            go.transform.localPosition = new Vector3(pos.x, pos.y - (sp.h >= HallH - 0.01f ? 1.0f : 0.5f), pos.z);
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = rng.NextDouble() < 0.25 ? new Color(1f, 0.9f, 0.7f) : new Color(0.85f, 0.95f, 1f);
            l.intensity = intensity;
            l.range = range;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            if (flicker)
            {
                var f = go.AddComponent<LightFlicker>(); // intensity ayarlandıktan sonra eklenir
                f.flickerChance = 0.1f;
            }
            if (culler != null) culler.Register(l);
        }

        // Birkaç kırmızı acil durum ışığı (koridor ve salonlarda)
        void AddEmergencyLights(System.Random rng)
        {
            var cands = new List<Space>();
            for (int i = 0; i < spaces.Count; i++)
                if (spaces[i].IsCorridor || spaces[i].h >= HallH - 0.01f || spaces[i].IsStair) cands.Add(spaces[i]);
            if (cands.Count == 0) return;
            int count = Mathf.Min(cands.Count, rng.Next(4, 8));
            for (int k = 0; k < count; k++)
            {
                var sp = cands[rng.Next(cands.Count)];
                var r = sp.rects[0];
                float h = sp.IsStair ? RoomH : sp.h;
                Vector3 pos;
                if (sp.IsStair)
                {
                    pos = new Vector3(r.x + r.w * 0.5f, StepH + h - 0.35f, r.z + r.d * 0.5f);
                }
                else pos = new Vector3(r.x + r.w * 0.5f + ((float)rng.NextDouble() - 0.5f) * (r.w - 1f), h - 0.35f,
                    r.z + r.d * 0.5f + ((float)rng.NextDouble() - 0.5f) * (r.d - 1f));
                Transform parent = sp.IsStair ? sp.rootUp : sp.root;
                if (sp.IsStair) pos.y -= StepH; // üst parçanın yerel koordinatı
                Block(parent, "Acil Lamba", pos, new Vector3(0.18f, 0.12f, 0.18f), ledRedMat, false);
                var go = new GameObject("Acil Işık");
                go.transform.SetParent(parent, false);
                go.transform.localPosition = pos - new Vector3(0f, 0.2f, 0f);
                var l = go.AddComponent<Light>();
                l.type = LightType.Point;
                l.color = new Color(1f, 0.12f, 0.08f);
                l.intensity = 0.9f;
                l.range = 5.5f;
                l.shadows = LightShadows.None;
                if (rng.NextDouble() < 0.3) { var f = go.AddComponent<LightFlicker>(); f.flickerChance = 0.06f; }
                if (culler != null) culler.Register(l);
            }
        }

        // ---------- Yardımcılar ----------

        // Birim küp ilkeli: ölçek = boyut. Çarpıştırıcı isteğe bağlı.
        GameObject Block(Transform parent, string name, Vector3 pos, Vector3 size, Material mat,
            bool collider = true, float yaw = 0f)
        {
            var go = new GameObject(name);
            var t = go.transform;
            t.SetParent(parent, false);
            t.localPosition = pos;
            t.localRotation = Quaternion.Euler(0f, yaw, 0f);
            t.localScale = size;
            go.AddComponent<MeshFilter>().sharedMesh = CubeMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (collider) go.AddComponent<BoxCollider>();
            return go;
        }

        // Ölçeksiz pivot (alt nesneleri döndürmek için)
        Transform Pivot(Transform parent, string name, Vector3 pos, Quaternion rot)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = rot;
            return go.transform;
        }

        Transform Prop(Transform parent, string name, Vector3 pos, float yaw)
            => Pivot(parent, name, pos, Quaternion.Euler(0f, yaw, 0f));

        static Vector3 Local(Vector3 pos, float yaw, Vector3 offset) => pos + Quaternion.Euler(0f, yaw, 0f) * offset;

        // Dikey silindir (çap, yükseklik)
        GameObject Cyl(Transform parent, string name, Vector3 pos, float dia, float height, Material mat, bool collider)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localScale = new Vector3(dia, height * 0.5f, dia); // silindir ilkeli 1 çap, 2 yükseklik
            go.AddComponent<MeshFilter>().sharedMesh = CylinderMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = mat;
            if (collider)
            {
                var box = go.AddComponent<BoxCollider>();
                box.size = new Vector3(1f, 2f, 1f);
            }
            return go;
        }

        void Pipe(Transform parent, Vector3 a, Vector3 b, float dia)
        {
            Vector3 d = b - a;
            float len = d.magnitude;
            if (len < 0.01f) return;
            var go = new GameObject("Boru");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = (a + b) * 0.5f;
            go.transform.localRotation = Quaternion.FromToRotation(Vector3.up, d / len);
            go.transform.localScale = new Vector3(dia, len * 0.5f, dia); // silindir ilkeli 2 m boyunda
            go.AddComponent<MeshFilter>().sharedMesh = CylinderMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = pipeMat;
        }

        static Mesh CubeMesh()
        {
            if (cubeMesh == null) cubeMesh = PrimitiveMesh(PrimitiveType.Cube);
            return cubeMesh;
        }

        static Mesh CylinderMesh()
        {
            if (cylinderMesh == null) cylinderMesh = PrimitiveMesh(PrimitiveType.Cylinder);
            return cylinderMesh;
        }

        static Mesh PrimitiveMesh(PrimitiveType type)
        {
            var tmp = GameObject.CreatePrimitive(type);
            var mesh = tmp.GetComponent<MeshFilter>().sharedMesh;
            DestroyImmediate(tmp); // çarpıştırıcısı fizik/NavMesh'e karışmasın
            return mesh;
        }

        // ---------- Malzemeler ----------

        // Atanmamış malzemeler için çalışma anı yedeği (üretim sırasında oluşturulur)
        void EnsureMaterials()
        {
            Ensure(ref concrete, new Color(0.17f, 0.17f, 0.18f), default);
            Ensure(ref ceilingMat, new Color(0.1f, 0.1f, 0.11f), default);
            Ensure(ref lineMat, new Color(0.85f, 0.68f, 0.08f), default);
            Ensure(ref metalMat, new Color(0.24f, 0.26f, 0.27f), default);
            Ensure(ref rustMat, new Color(0.3f, 0.16f, 0.1f), default);
            Ensure(ref crateMat, new Color(0.33f, 0.24f, 0.13f), default);
            Ensure(ref deskMat, new Color(0.22f, 0.2f, 0.18f), default);
            Ensure(ref doorFrameMat, new Color(0.08f, 0.08f, 0.09f), default);
            Ensure(ref doorMat, new Color(0.3f, 0.16f, 0.1f), default);
            Ensure(ref screenMat, new Color(0.03f, 0.1f, 0.08f), new Color(0.2f, 1.2f, 0.9f));
            Ensure(ref screenAmberMat, new Color(0.15f, 0.08f, 0.02f), new Color(1.3f, 0.7f, 0.15f));
            Ensure(ref stripOnMat, new Color(0.8f, 0.9f, 1f), new Color(2f, 2.2f, 2.4f));
            Ensure(ref stripOffMat, new Color(0.08f, 0.08f, 0.09f), default);
            Ensure(ref ledGreenMat, new Color(0.05f, 0.3f, 0.05f), new Color(0.2f, 2f, 0.3f));
            Ensure(ref ledRedMat, new Color(0.3f, 0.05f, 0.05f), new Color(2f, 0.2f, 0.15f));
            Ensure(ref ledAmberMat, new Color(0.3f, 0.2f, 0.03f), new Color(2f, 1.2f, 0.2f));
            Ensure(ref exitSignMat, new Color(0.05f, 0.3f, 0.1f), new Color(0.2f, 2f, 0.5f));
            Ensure(ref pipeMat, new Color(0.25f, 0.2f, 0.17f), default);
            Ensure(ref cableMat, new Color(0.03f, 0.03f, 0.03f), default);
            Ensure(ref paperMat, new Color(0.7f, 0.68f, 0.6f), default);
            Ensure(ref fabricMat, new Color(0.18f, 0.2f, 0.17f), default);
            Ensure(ref blackMat, new Color(0.04f, 0.04f, 0.045f), default);

            // Boya çeşitleri: alt yarı renkli, üst yarı kirli beyaz/gri; zemin çeşitleri
            Color[] lower =
            {
                new Color(0.16f, 0.27f, 0.26f), new Color(0.24f, 0.28f, 0.17f), new Color(0.2f, 0.24f, 0.3f),
                new Color(0.34f, 0.27f, 0.14f), new Color(0.2f, 0.2f, 0.21f)
            };
            Color[] upper =
            {
                new Color(0.62f, 0.62f, 0.58f), new Color(0.58f, 0.58f, 0.5f), new Color(0.55f, 0.57f, 0.6f),
                new Color(0.6f, 0.57f, 0.5f), new Color(0.45f, 0.45f, 0.44f)
            };
            paintLower = new Material[PaintCount];
            paintUpper = new Material[PaintCount];
            for (int i = 0; i < PaintCount; i++)
            {
                Material a = null, b = null;
                Ensure(ref a, lower[i], default);
                Ensure(ref b, upper[i], default);
                paintLower[i] = a;
                paintUpper[i] = b;
            }
            floorMats = new Material[3];
            floorMats[0] = concrete;
            Material f1 = null, f2 = null;
            Ensure(ref f1, new Color(0.12f, 0.12f, 0.13f), default);
            Ensure(ref f2, new Color(0.22f, 0.2f, 0.17f), default);
            floorMats[1] = f1;
            floorMats[2] = f2;
        }

        static void Ensure(ref Material m, Color color, Color emission)
        {
            if (m != null) return;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            else m.color = color;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.15f);
            if (emission.maxColorComponent > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
            }
        }
    }
}
