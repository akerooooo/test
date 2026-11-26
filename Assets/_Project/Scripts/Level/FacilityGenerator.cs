using System.Collections.Generic;
using MimicGame.Environment;
using MimicGame.Mimic;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MimicGame.Level
{
    public enum RoomKind
    {
        Entrance, Corridor, Stair,
        ControlRoom, RadioHall, RadioRoom, Warehouse,
        Storage, Office, Bunk, Generator, Locker, Workshop, Closet, PipeRoom, Mess
    }

    /// <summary>Yapay zekâ için oda bilgisi (dünya uzayında).</summary>
    public sealed class RoomInfo
    {
        public RoomKind kind;
        public string theme;
        public Vector3 center;
        public Bounds bounds;
        public bool dark;
        public bool corridor;
        public bool large;
        public int level;
    }

    /// <summary>
    /// Tesisin iç mekânını tohumdan (seed) üretir (çok katlı: giriş katı, bir üst, iki alt kat).
    /// Sunucu tohumu seçer; her istemci aynı yerleşimi yalnızca System.Random(seed) ile ilkel şekillerden kurar.
    /// İç mekân haritanın çok altındadır (y -130 ile -70 arası). Sunucu üretimden sonra NavMesh'i yeniler,
    /// ganimetleri ağ nesnesi olarak doğurur. Giriş holü her turda aynıdır; geri kalanı tohuma bağlıdır.
    /// </summary>
    public partial class FacilityGenerator : NetworkBehaviour
    {
        // ---------- Genel durum ----------

        /// <summary>İç mekân bu yüksekliğin altındadır (dış dünyayla çakışmasın).</summary>
        public const float InteriorThresholdY = -50f;

        public static FacilityGenerator Instance { get; private set; }
        /// <summary>Tesis yerelde üretildi ve çarpıştırıcılar hazır mı.</summary>
        public static bool Generated { get; private set; }
        public static event System.Action OnGenerated;

        /// <summary>Dünya konumları: giriş holünden uzak odaların merkezleri.</summary>
        public static readonly List<Vector3> MimicSpawns = new List<Vector3>();
        /// <summary>Dünya konumları: tüm oda merkezleri / koridor noktaları (taklitçi gezintisi için).</summary>
        public static readonly List<Vector3> WanderPoints = new List<Vector3>();
        /// <summary>Oda listesi (dünya uzayında merkez, sınır, tema).</summary>
        public static readonly List<RoomInfo> Rooms = new List<RoomInfo>();
        /// <summary>Kapı açıklıklarının dünya konumları (yapay zekâ yavaşlama/duraklama için).</summary>
        public static readonly List<Vector3> DoorPoints = new List<Vector3>();

        public static bool IsInteriorPoint(Vector3 p) => p.y < InteriorThresholdY;

        /// <summary>Rastgele bir iç mekân noktası (sunucu tarafı, deterministik olmak zorunda değil).</summary>
        public static bool TryGetRandomPoint(out Vector3 p)
        {
            p = Vector3.zero;
            if (!Generated || WanderPoints.Count == 0) return false;
            p = WanderPoints[Random.Range(0, WanderPoints.Count)];
            p += new Vector3(Random.Range(-1f, 1f), 0f, Random.Range(-1f, 1f));
            return true;
        }

        /// <summary>Dünya konumunu içeren oda (yoksa null).</summary>
        public static RoomInfo RoomAt(Vector3 p)
        {
            for (int i = 0; i < Rooms.Count; i++)
                if (Rooms[i].bounds.Contains(p)) return Rooms[i];
            return null;
        }

        // ---------- Ayarlar ----------

        [Header("Dışarı çıkış noktası")]
        public Vector3 outsideExitPoint = new Vector3(0f, 0.05f, 7.5f);
        public float outsideExitYaw = 180f;

        [Header("İçerik")]
        public LootTable lootTable;
        [Tooltip("Toplam oda sayısı (koridor ve merdiven hariç) aralığı.")]
        public int roomsMin = 22;
        public int roomsMax = 30;
        public int lootMin = 26;
        public int lootMax = 40;

        [Header("Malzemeler (builder atar; boşsa çalışma anında üretilir)")]
        public Material concrete;
        public Material ceilingMat;
        public Material lineMat;
        public Material metalMat;
        public Material rustMat;
        public Material crateMat;
        public Material deskMat;
        public Material doorFrameMat;
        public Material doorMat;
        public Material screenMat;
        public Material screenAmberMat;
        public Material stripOnMat;
        public Material stripOffMat;
        public Material ledGreenMat;
        public Material ledRedMat;
        public Material ledAmberMat;
        public Material exitSignMat;
        public Material pipeMat;
        public Material cableMat;
        public Material paperMat;
        public Material fabricMat;
        public Material blackMat;

        public Vector3 ArrivalPoint => genRoot != null ? genRoot.transform.TransformPoint(arrivalLocal) : transform.position;
        public float ArrivalYaw => 0f;
        public Vector3 OutsideExitPoint => outsideExitPoint;
        public float OutsideExitYaw => outsideExitYaw;
        public int Seed => seed.Value;
        public int RoomCount => spaces.Count;
        public int LootPointCount => lootPoints.Count;

        // ---------- Sabitler ----------

        const int G = 128;          // ızgara boyu (1 m hücre)
        const int Org = 48;         // ızgara orijin kayması
        const int OG = G * 2;       // doluluk ızgarası (0.5 m)
        const int Levels = 4;
        const int EntLv = 1;        // giriş katı dizini
        const float StepH = 4f;     // kat aralığı
        const float WallT = 0.2f;
        const float SlabT = 0.15f;
        const float CorridorH = 3.0f;
        const float RoomH = 3.2f;
        const float HallH = 6.0f;
        const float OpenW = 1.6f;   // sıradan kapı açıklığı
        const float OpenHt = 2.4f;

        // Kat tabanları (üretici-yerel y): 0 üst kat, 1 giriş, 2 ve 3 alt katlar
        static readonly float[] LevelBase = { 4f, 0f, -4f, -8f };

        struct RectI
        {
            public int x, z, w, d;
            public RectI(int x, int z, int w, int d) { this.x = x; this.z = z; this.w = w; this.d = d; }
        }

        sealed class Space
        {
            public int id;                 // 1 tabanlı
            public RoomKind kind;
            public int lv;                 // (merdiven için: üst kat)
            public int lvLow;              // merdiven için alt kat; diğerlerinde lv
            public float h;
            public int paint;
            public int floorVar;
            public int variant;
            public float density;
            public bool seed;
            public bool lit;
            public bool dark => !lit;
            public bool nearUpper;         // merdiven: yakın uç üst katta mı
            public int dirX, dirZ;         // merdiven: yakın uçtan uzak uca yön
            public readonly List<RectI> rects = new List<RectI>(2);
            public Transform root, rootUp;
            public bool IsStair => kind == RoomKind.Stair;
            public bool IsCorridor => kind == RoomKind.Corridor;
        }

        sealed class DoorRec
        {
            public int lv;
            public bool alongX;
            public int line, start, n;
            public float width, height;
            public int kind;               // 1 kapı, 2 açık geçit, 3 çıkış kapısı
            public int a, b;
        }

        sealed class Grid
        {
            public readonly int[] owner = new int[G * G];
            public readonly byte[] hType = new byte[G * G];
            public readonly byte[] vType = new byte[G * G];
            public readonly bool[] occ = new bool[OG * OG];
        }

        // Sunucu yazar, herkes okur
        readonly NetworkVariable<int> seed = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly List<Space> spaces = new List<Space>();
        readonly List<DoorRec> doors = new List<DoorRec>();
        readonly List<Vector3> lootPoints = new List<Vector3>(); // üretici-yerel konumlar (y dahil)
        Grid[] grids;

        GameObject genRoot;
        Transform[] levelRoots;
        Transform[] wallRoots;
        LightCuller culler;
        Vector3 arrivalLocal = new Vector3(4.5f, 0.05f, 1.8f);
        int generatedSeed;
        bool lootSpawned;
        RoomKind lastKind = RoomKind.Entrance;
        int radioHallCount, radioRoomCount, generatorCount, controlCount;

        // ---------- Ağ yaşam döngüsü ----------

        public override void OnNetworkSpawn()
        {
            Instance = this;
            seed.OnValueChanged += OnSeedChanged;
            if (IsServer) seed.Value = NewSeed(); // her sunucu başlangıcında yeni tohum
            TryGenerate();
        }

        public override void OnNetworkDespawn()
        {
            seed.OnValueChanged -= OnSeedChanged;
            ClearGenerated();
            if (Instance == this) Instance = null;
        }

        public override void OnDestroy()
        {
            if (Instance == this) { ClearGenerated(); Instance = null; }
            base.OnDestroy();
        }

        // Zaman, GUID ve Unity rastgeleliğinden karışık, sıfırdan farklı, pozitif tohum
        static int NewSeed()
        {
            long t = System.DateTime.UtcNow.Ticks;
            int a = System.Environment.TickCount;
            int b = System.Guid.NewGuid().GetHashCode();
            int c = Random.Range(1, int.MaxValue);
            int s = (int)((t ^ (t >> 32)) & 0x7FFFFFFF) ^ (a * 73856093) ^ b ^ c;
            s &= 0x7FFFFFFF;
            return s == 0 ? 1 : s;
        }

        void OnSeedChanged(int oldValue, int newValue) => TryGenerate();

        void TryGenerate()
        {
            int s = seed.Value;
            if (s == 0 || (Generated && generatedSeed == s)) return;
            Generate(s);
        }

        void Update()
        {
            if (!IsSpawned) return;

            // Üretimden sonra sunucu ganimetleri doğurur (spawn döngüsünün dışında)
            if (IsServer && Generated && !lootSpawned)
            {
                lootSpawned = true;
                SpawnLoot();
            }

            if (IsServer && NetworkManager != null && NetworkManager.IsHost && Keyboard.current != null &&
                Keyboard.current.f10Key.wasPressedThisFrame)
                Debug.Log("[Tesis] Tohum " + seed.Value + ", " + spaces.Count + " alan, " + lootPoints.Count + " ganimet noktası");
        }

        // ---------- Üretim ----------

        void Generate(int s)
        {
            ClearGenerated();
            generatedSeed = s;
            var rng = new System.Random(s);
            EnsureMaterials();

            genRoot = new GameObject("Tesis (üretilen)");
            genRoot.transform.SetParent(transform, false);
            culler = genRoot.AddComponent<LightCuller>();

            grids = new Grid[Levels];
            for (int i = 0; i < Levels; i++) grids[i] = new Grid();
            levelRoots = new Transform[Levels];
            wallRoots = new Transform[Levels];
            for (int i = 0; i < Levels; i++)
            {
                var lr = new GameObject("Kat " + i);
                lr.transform.SetParent(genRoot.transform, false);
                lr.transform.localPosition = new Vector3(0f, LevelBase[i], 0f);
                levelRoots[i] = lr.transform;
                var wr = new GameObject("Duvarlar");
                wr.transform.SetParent(lr.transform, false);
                wallRoots[i] = wr.transform;
            }

            BuildLayout(rng);
            AssignAppearance(rng);
            CreateSpaceRoots();
            ReserveDoorZones();
            BuildAllSlabs();
            for (int li = 0; li < Levels; li++) BuildLevelWalls(li);
            BuildExitDoor();

            foreach (var sp in spaces)
            {
                Decorate(sp, rng);
                AddSpaceLights(sp, rng);
            }
            AddEmergencyLights(rng);

            CollectPoints();
            CombineStatic();
            Physics.SyncTransforms();

            if (IsServer) RebuildNavMesh();

            Generated = true;
            OnGenerated?.Invoke();
            Debug.Log("[Tesis] Oluşturuldu: tohum " + s + ", " + spaces.Count + " alan, " + lootPoints.Count + " ganimet noktası");
        }

        void ClearGenerated()
        {
            Generated = false;
            lootSpawned = false;
            generatedSeed = 0;
            MimicSpawns.Clear();
            WanderPoints.Clear();
            Rooms.Clear();
            DoorPoints.Clear();
            spaces.Clear();
            doors.Clear();
            lootPoints.Clear();
            grids = null;
            lastKind = RoomKind.Entrance;
            radioHallCount = radioRoomCount = generatorCount = controlCount = 0;
            if (genRoot != null) Destroy(genRoot);
            genRoot = null;
            culler = null;
        }

        // ---------- Izgara yardımcıları ----------

        static bool InGrid(int x, int z)
        {
            int i = x + Org, j = z + Org;
            return i >= 0 && j >= 0 && i < G && j < G;
        }

        int Owner(int li, int x, int z)
        {
            if (!InGrid(x, z)) return 0;
            int v = grids[li].owner[(z + Org) * G + (x + Org)];
            return v > 0 ? v : 0;
        }

        int RawOwner(int li, int x, int z)
        {
            if (!InGrid(x, z)) return -1;
            return grids[li].owner[(z + Org) * G + (x + Org)];
        }

        byte HT(int li, int x, int z) => InGrid(x, z) ? grids[li].hType[(z + Org) * G + (x + Org)] : (byte)0;
        byte VT(int li, int x, int z) => InGrid(x, z) ? grids[li].vType[(z + Org) * G + (x + Org)] : (byte)0;

        bool RectFree(int li, RectI r)
        {
            if (r.w < 1 || r.d < 1) return false;
            if (r.x < -Org + 3 || r.z < -Org + 3 || r.x + r.w > G - Org - 3 || r.z + r.d > G - Org - 3) return false;
            for (int i = 0; i < r.w; i++)
                for (int j = 0; j < r.d; j++)
                    if (RawOwner(li, r.x + i, r.z + j) != 0) return false;
            return true;
        }

        void FillCells(int li, RectI r, int id)
        {
            for (int i = 0; i < r.w; i++)
                for (int j = 0; j < r.d; j++)
                    grids[li].owner[(r.z + j + Org) * G + (r.x + i + Org)] = id;
        }

        void MarkBlocked(int li, RectI r)
        {
            for (int i = 0; i < r.w; i++)
                for (int j = 0; j < r.d; j++)
                {
                    int idx = (r.z + j + Org) * G + (r.x + i + Org);
                    if (grids[li].owner[idx] == 0) grids[li].owner[idx] = -1;
                }
        }

        static bool IsTall(RoomKind k) => k == RoomKind.ControlRoom || k == RoomKind.RadioHall || k == RoomKind.Warehouse;

        static float HeightFor(RoomKind k)
        {
            if (k == RoomKind.Corridor) return CorridorH;
            if (IsTall(k)) return HallH;
            return RoomH;
        }

        float HeightAt(int li, int id)
        {
            if (id <= 0) return 0f;
            var sp = spaces[id - 1];
            if (sp.IsStair) return li == sp.lv ? RoomH : StepH;
            return sp.h;
        }

        bool IsStairLow(int li, int id) => id > 0 && spaces[id - 1].IsStair && li == spaces[id - 1].lvLow;

        bool CanPlace(int li, RoomKind kind, RectI r)
        {
            if (!RectFree(li, r)) return false;
            if (IsTall(kind) && li > 0 && !RectFree(li - 1, r)) return false;
            return true;
        }

        Space AddSpace(RoomKind kind, int li, RectI r)
        {
            var sp = new Space { id = spaces.Count + 1, kind = kind, lv = li, lvLow = li, h = HeightFor(kind), density = 1f };
            sp.rects.Add(r);
            spaces.Add(sp);
            FillCells(li, r, sp.id);
            if (IsTall(kind) && li > 0) MarkBlocked(li - 1, r);
            return sp;
        }

        void AddWing(Space sp, RectI r)
        {
            sp.rects.Add(r);
            FillCells(sp.lv, r, sp.id);
            if (IsTall(sp.kind) && sp.lv > 0) MarkBlocked(sp.lv - 1, r);
        }

        // Kapı / açık geçit işaretle: alongX = kenar X boyunca uzanır (z = line çizgisinde)
        void AddDoorAt(int li, bool alongX, int line, int start, int n, int kind, float width, float height)
        {
            var g = grids[li];
            for (int i = 0; i < n; i++)
            {
                if (alongX) g.hType[(line + Org) * G + (start + i + Org)] = (byte)kind;
                else g.vType[(start + i + Org) * G + (line + Org)] = (byte)kind;
            }
            var dr = new DoorRec { lv = li, alongX = alongX, line = line, start = start, n = n, width = width, height = height, kind = kind };
            if (alongX) { dr.a = Owner(li, start, line - 1); dr.b = Owner(li, start, line); }
            else { dr.a = Owner(li, line - 1, start); dr.b = Owner(li, line, start); }
            doors.Add(dr);
        }

        // ---------- Koridor geometrisi ----------

        static bool AlongZ(Space c) => c.rects[0].d > c.rects[0].w;

        static void CorridorRange(Space c, out int start, out int end)
        {
            var r = c.rects[0];
            if (AlongZ(c)) { start = r.z; end = r.z + r.d; }
            else { start = r.x; end = r.x + r.w; }
        }

        // Koridorun yan tarafına bitişik dikdörtgen: t koridor ekseni boyunca başlangıç, along o eksendeki uzunluk,
        // depth dışa doğru derinlik, off dışa doğru kayma
        RectI RectFor(Space c, int side, int t, int along, int depth, int off)
        {
            var r = c.rects[0];
            if (AlongZ(c))
            {
                if (side > 0) return new RectI(r.x + r.w + off, t, depth, along);
                return new RectI(r.x - off - depth, t, depth, along);
            }
            if (side > 0) return new RectI(t, r.z + r.d + off, along, depth);
            return new RectI(t, r.z - off - depth, along, depth);
        }

        void MarkSide(int li, Space c, int side, int start, int n, int kind, float width, float height)
        {
            var r = c.rects[0];
            bool alongZ = AlongZ(c);
            int line = alongZ ? (side > 0 ? r.x + r.w : r.x) : (side > 0 ? r.z + r.d : r.z);
            AddDoorAt(li, !alongZ, line, start, n, kind, width, height);
        }

        List<Space> CorridorsOn(int li)
        {
            var list = new List<Space>();
            for (int i = 0; i < spaces.Count; i++)
                if (spaces[i].IsCorridor && spaces[i].lv == li) list.Add(spaces[i]);
            return list;
        }

        // ---------- Yerleşim ----------

        void BuildLayout(System.Random rng)
        {
            int total = rng.Next(roomsMin, roomsMax + 1);
            int r1 = Mathf.Max(7, Mathf.RoundToInt(total * 0.36f));
            int r0 = Mathf.Max(4, Mathf.RoundToInt(total * 0.18f));
            int r2 = Mathf.Max(5, Mathf.RoundToInt(total * 0.28f));
            int r3 = Mathf.Max(3, total - r1 - r0 - r2);

            // Giriş holü: her turda sabit
            AddSpace(RoomKind.Entrance, EntLv, new RectI(0, 0, 9, 7));
            var m = AddSpace(RoomKind.Corridor, EntLv, new RectI(3, 7, 3, rng.Next(28, 35)));
            AddDoorAt(EntLv, true, 7, 3, 3, 2, 0f, 0f); // hol - ana koridor açık geçidi
            AddDoorAt(EntLv, true, 0, 3, 3, 3, FacilityDoorSpec.Width, FacilityDoorSpec.Height); // çıkış kapısı
            GrowCorridor(EntLv, m, rng, 0);
            TryEndHall(EntLv, m, true, rng, RoomKind.ControlRoom);

            // Katlar arası merdivenler (giriş katından yukarı ve aşağı)
            int up = rng.Next(2, 4), down = rng.Next(2, 4);
            for (int i = 0; i < up; i++) TryPlaceStairs(EntLv, 0, rng);
            for (int i = 0; i < down; i++) TryPlaceStairs(EntLv, 2, rng);
            FillRooms(EntLv, r1, rng);

            GrowSeeds(0, rng);
            FillRooms(0, r0, rng);

            GrowSeeds(2, rng);
            int down2 = rng.Next(1, 3) + 1;
            for (int i = 0; i < down2; i++) TryPlaceStairs(2, 3, rng);
            FillRooms(2, r2, rng);

            GrowSeeds(3, rng);
            FillRooms(3, r3, rng);

            AddExtraDoors(rng);
        }

        // Koridora yan kollar: T ve artı kavşaklar, kısa çıkmaz kollar
        void GrowCorridor(int li, Space c, System.Random rng, int depthLevel)
        {
            CorridorRange(c, out int start, out int end);
            int len0 = end - start;
            if (len0 < 12) return;
            int nBr = depthLevel == 0 ? rng.Next(2, 5) : rng.Next(0, 2);
            var usedT = new List<int>();
            for (int i = 0; i < nBr; i++)
            {
                int t = 0;
                bool ok = false;
                for (int attempt = 0; attempt < 8 && !ok; attempt++)
                {
                    t = start + 4 + rng.Next(Mathf.Max(1, len0 - 9));
                    ok = true;
                    for (int u = 0; u < usedT.Count; u++) if (Mathf.Abs(usedT[u] - t) < 7) ok = false;
                }
                if (!ok) continue;
                usedT.Add(t);

                int side = rng.Next(2) == 0 ? 1 : -1;
                bool cross = rng.NextDouble() < 0.35;
                int sides = cross ? 2 : 1;
                for (int k = 0; k < sides; k++)
                {
                    int sd = k == 0 ? side : -side;
                    int len = rng.NextDouble() < 0.25 ? rng.Next(5, 9) : rng.Next(9, 24);
                    RectI r = RectFor(c, sd, t, 3, len, 0);
                    while (len >= 5 && !RectFree(li, r)) { len -= 2; r = RectFor(c, sd, t, 3, len, 0); }
                    if (len < 5) continue;
                    var b = AddSpace(RoomKind.Corridor, li, r);
                    MarkSide(li, c, sd, t, 3, 2, 0f, 0f);
                    if (depthLevel < 1 && len >= 10 && rng.NextDouble() < 0.45) GrowCorridor(li, b, rng, depthLevel + 1);
                }
            }
        }

        // Koridor ucuna büyük salon ekle
        bool TryEndHall(int li, Space c, bool atEnd, System.Random rng, RoomKind kind)
        {
            int along = kind == RoomKind.ControlRoom ? 13 : 11;
            int depth = kind == RoomKind.ControlRoom ? 10 : 9;
            var r = c.rects[0];
            bool alongZ = AlongZ(c);
            RectI hr;
            if (alongZ)
                hr = new RectI(r.x - (along - 3) / 2, atEnd ? r.z + r.d : r.z - depth, along, depth);
            else
                hr = new RectI(atEnd ? r.x + r.w : r.x - depth, r.z - (along - 3) / 2, depth, along);
            if (!CanPlace(li, kind, hr)) return false;
            var hall = AddSpace(kind, li, hr);
            if (kind == RoomKind.RadioHall) radioHallCount++;
            if (kind == RoomKind.ControlRoom) controlCount++;
            int line = atEnd ? (alongZ ? r.z + r.d : r.x + r.w) : (alongZ ? r.z : r.x);
            int ds = (alongZ ? r.x : r.z) + rng.Next(2);
            AddDoorAt(li, alongZ, line, ds, 2, 1, OpenW, OpenHt);
            return hall != null;
        }

        void GrowSeeds(int li, System.Random rng)
        {
            var list = CorridorsOn(li);
            var seeds = new List<Space>();
            for (int i = 0; i < list.Count; i++) if (list[i].seed) seeds.Add(list[i]);
            for (int i = 0; i < seeds.Count; i++)
            {
                GrowCorridor(li, seeds[i], rng, 0);
                if (rng.NextDouble() < 0.7)
                {
                    RoomKind hk = (radioHallCount < 1 && rng.Next(2) == 0) ? RoomKind.RadioHall : RoomKind.Warehouse;
                    if (!TryEndHall(li, seeds[i], true, rng, hk) && hk != RoomKind.Warehouse)
                        TryEndHall(li, seeds[i], true, rng, RoomKind.Warehouse);
                }
            }
        }

        // ---------- Merdivenler ----------

        // Yakın kattaki bir koridorun yanına 3x8 merdiven boşluğu, uzak katta onun ardına tohum koridoru koyar
        bool TryPlaceStairs(int nearLv, int farLv, System.Random rng)
        {
            var corrs = CorridorsOn(nearLv);
            if (corrs.Count == 0) return false;
            for (int attempt = 0; attempt < 40; attempt++)
            {
                var c = corrs[rng.Next(corrs.Count)];
                CorridorRange(c, out int start, out int end);
                if (end - start < 8) continue;
                int side = rng.Next(2) == 0 ? 1 : -1;
                int t = start + 1 + rng.Next(end - start - 4);
                if (t + 3 > end - 1) continue;
                RectI shaft = RectFor(c, side, t, 3, 8, 0);
                int seedLen = rng.Next(11, 18);
                RectI seedR = RectFor(c, side, t, 3, seedLen, 8);
                if (!RectFree(nearLv, shaft) || !RectFree(farLv, shaft)) continue;
                if (!RectFree(farLv, seedR)) continue;

                int dx = 0, dz = 0;
                if (AlongZ(c)) dx = side; else dz = side;
                var st = new Space
                {
                    id = spaces.Count + 1, kind = RoomKind.Stair,
                    lv = Mathf.Min(nearLv, farLv), lvLow = Mathf.Max(nearLv, farLv),
                    h = RoomH, nearUpper = nearLv < farLv, dirX = dx, dirZ = dz, density = 1f
                };
                st.rects.Add(shaft);
                spaces.Add(st);
                FillCells(nearLv, shaft, st.id);
                FillCells(farLv, shaft, st.id);
                var sc = AddSpace(RoomKind.Corridor, farLv, seedR);
                sc.seed = true;

                // Yakın uç: koridorun yan duvarında 3 m'lik açık geçit
                MarkSide(nearLv, c, side, t, 3, 2, 0f, 0f);
                // Uzak uç: tohum koridoruna açık geçit
                if (dx != 0)
                    AddDoorAt(farLv, false, dx > 0 ? shaft.x + shaft.w : shaft.x, shaft.z, 3, 2, 0f, 0f);
                else
                    AddDoorAt(farLv, true, dz > 0 ? shaft.z + shaft.d : shaft.z, shaft.x, 3, 2, 0f, 0f);
                return true;
            }
            return false;
        }

        // ---------- Odalar ----------

        struct Slot
        {
            public Space corr;
            public int side;
            public int cursor;
        }

        static readonly RoomKind[] RoomKinds =
        {
            RoomKind.Storage, RoomKind.Office, RoomKind.Bunk, RoomKind.Generator, RoomKind.Locker,
            RoomKind.Workshop, RoomKind.Closet, RoomKind.PipeRoom, RoomKind.Mess, RoomKind.Warehouse, RoomKind.RadioRoom
        };
        static readonly int[] RoomWeights = { 6, 5, 5, 3, 4, 4, 4, 3, 3, 2, 1 };

        RoomKind PickKind(System.Random rng)
        {
            for (int tries = 0; tries < 8; tries++)
            {
                int total = 0;
                for (int i = 0; i < RoomWeights.Length; i++) total += RoomWeights[i];
                int r = rng.Next(total);
                RoomKind k = RoomKinds[0];
                for (int i = 0; i < RoomWeights.Length; i++)
                {
                    r -= RoomWeights[i];
                    if (r < 0) { k = RoomKinds[i]; break; }
                }
                if (k == lastKind) continue; // art arda aynı şablon olmasın
                if (k == RoomKind.Generator && generatorCount >= 2) continue;
                if (k == RoomKind.RadioRoom && radioRoomCount >= 1) continue;
                return k;
            }
            return RoomKind.Storage == lastKind ? RoomKind.Office : RoomKind.Storage;
        }

        static void SizeFor(RoomKind k, System.Random rng, out int along, out int depth, out int shape)
        {
            shape = 0; // 0 dikdörtgen, 1 L kanadı
            switch (k)
            {
                case RoomKind.Storage: along = rng.Next(6, 10); depth = rng.Next(5, 8); if (rng.NextDouble() < 0.35) shape = 1; break;
                case RoomKind.Office: along = rng.Next(5, 7); depth = rng.Next(4, 6); if (rng.NextDouble() < 0.2) shape = 1; break;
                case RoomKind.Bunk:
                    if (rng.NextDouble() < 0.5) { along = rng.Next(9, 12); depth = 4; }
                    else { along = rng.Next(6, 9); depth = rng.Next(5, 7); }
                    break;
                case RoomKind.Generator: along = 8; depth = rng.Next(6, 9); break;
                case RoomKind.Locker: along = rng.Next(5, 9); depth = rng.Next(4, 6); break;
                case RoomKind.Workshop: along = rng.Next(7, 10); depth = rng.Next(5, 7); if (rng.NextDouble() < 0.3) shape = 1; break;
                case RoomKind.Closet: along = 4; depth = rng.Next(3, 5); break;
                case RoomKind.PipeRoom: along = rng.Next(5, 8); depth = rng.Next(5, 7); break;
                case RoomKind.Mess: along = rng.Next(8, 10); depth = rng.Next(6, 8); break;
                case RoomKind.Warehouse: along = 11; depth = 9; break;
                case RoomKind.RadioRoom: along = 6; depth = 5; break;
                default: along = 6; depth = 5; break;
            }
        }

        void FillRooms(int li, int target, System.Random rng)
        {
            var slots = new List<Slot>();
            var corrs = CorridorsOn(li);
            for (int i = 0; i < corrs.Count; i++)
            {
                CorridorRange(corrs[i], out int start, out int end);
                for (int sd = -1; sd <= 1; sd += 2)
                    slots.Add(new Slot { corr = corrs[i], side = sd, cursor = start + rng.Next(3) });
            }

            int placed = 0, guard = 0;
            while (placed < target && slots.Count > 0 && guard++ < 900)
            {
                int si = rng.Next(slots.Count);
                var s = slots[si];
                CorridorRange(s.corr, out int cs, out int ce);
                if (s.cursor > ce - 4)
                {
                    slots[si] = slots[slots.Count - 1];
                    slots.RemoveAt(slots.Count - 1);
                    continue;
                }

                bool ok = false;
                for (int k = 0; k < 3 && !ok; k++)
                {
                    RoomKind kind = PickKind(rng);
                    ok = TryPlaceRoom(li, s.corr, s.side, s.cursor, ce, kind, rng, out int used);
                    if (ok) { s.cursor += used + rng.Next(0, 2); lastKind = kind; placed++; }
                }
                if (!ok) s.cursor += 2;
                slots[si] = s;
            }
        }

        bool TryPlaceRoom(int li, Space c, int side, int t, int corrEnd, RoomKind kind, System.Random rng, out int used)
        {
            used = 0;
            SizeFor(kind, rng, out int along, out int depth, out int shape);
            if (t + along > corrEnd) along = corrEnd - t;
            if (along < 4) return false;
            RectI r = RectFor(c, side, t, along, depth, 0);
            if (!CanPlace(li, kind, r)) return false;

            var sp = AddSpace(kind, li, r);
            if (kind == RoomKind.Generator) generatorCount++;
            if (kind == RoomKind.RadioRoom) radioRoomCount++;

            // L biçimli oda: uzak duvara bitişik bir kanat
            if (shape == 1 && along >= 6)
            {
                int bw = rng.Next(3, along - 2);
                int bd = rng.Next(3, 5);
                int tb = rng.Next(2) == 0 ? t : t + along - bw;
                RectI wing = RectFor(c, side, tb, bw, bd, depth);
                if (RectFree(li, wing)) AddWing(sp, wing);
            }

            int ds = t + 1 + rng.Next(Mathf.Max(1, along - 3));
            if (ds + 2 > t + along - 1) ds = t + along - 3;
            MarkSide(li, c, side, ds, 2, 1, OpenW, OpenHt);
            used = along;
            return true;
        }

        // Komşu alanlar arasına ek kapılar (döngüler / birden çok rota)
        void AddExtraDoors(System.Random rng)
        {
            var pairs = new List<long>();
            for (int i = 0; i < doors.Count; i++)
            {
                var d = doors[i];
                if (d.a > 0 && d.b > 0) pairs.Add(PairKey(d.a, d.b));
            }

            for (int li = 0; li < Levels; li++)
            {
                for (int z = -Org + 2; z < G - Org - 2; z++)
                {
                    // yatay duvar çizgileri (kenar X boyunca, z çizgisinde)
                    int x = -Org + 2;
                    while (x < G - Org - 2)
                    {
                        int a = Owner(li, x, z - 1), b = Owner(li, x, z);
                        if (a > 0 && b > 0 && a != b && HT(li, x, z) == 0)
                        {
                            int x2 = x;
                            while (x2 + 1 < G - Org - 2 && Owner(li, x2 + 1, z - 1) == a && Owner(li, x2 + 1, z) == b && HT(li, x2 + 1, z) == 0) x2++;
                            TryExtraDoor(li, true, z, x, x2 - x + 1, a, b, pairs, rng);
                            x = x2 + 1;
                        }
                        else x++;
                    }
                }
                for (int xl = -Org + 2; xl < G - Org - 2; xl++)
                {
                    int z = -Org + 2;
                    while (z < G - Org - 2)
                    {
                        int a = Owner(li, xl - 1, z), b = Owner(li, xl, z);
                        if (a > 0 && b > 0 && a != b && VT(li, xl, z) == 0)
                        {
                            int z2 = z;
                            while (z2 + 1 < G - Org - 2 && Owner(li, xl - 1, z2 + 1) == a && Owner(li, xl, z2 + 1) == b && VT(li, xl, z2 + 1) == 0) z2++;
                            TryExtraDoor(li, false, xl, z, z2 - z + 1, a, b, pairs, rng);
                            z = z2 + 1;
                        }
                        else z++;
                    }
                }
            }
        }

        static long PairKey(int a, int b) => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        void TryExtraDoor(int li, bool alongX, int line, int start, int len, int a, int b, List<long> pairs, System.Random rng)
        {
            if (len < 4) return;
            var sa = spaces[a - 1];
            var sb = spaces[b - 1];
            if (sa.kind == RoomKind.Entrance || sb.kind == RoomKind.Entrance || sa.IsStair || sb.IsStair) return;
            if (sa.IsCorridor && sb.IsCorridor) return;
            long key = PairKey(a, b);
            if (pairs.Contains(key)) return;
            if (rng.NextDouble() > 0.3) return;
            int ds = start + 1 + rng.Next(Mathf.Max(1, len - 3));
            if (ds + 2 > start + len - 1) ds = start + len - 3;
            pairs.Add(key);
            AddDoorAt(li, alongX, line, ds, 2, 1, OpenW, OpenHt);
        }

        // ---------- Görünüm atamaları ----------

        void AssignAppearance(System.Random rng)
        {
            for (int i = 0; i < spaces.Count; i++)
            {
                var sp = spaces[i];
                sp.paint = sp.kind == RoomKind.Entrance ? 0 : rng.Next(PaintCount);
                sp.floorVar = sp.kind == RoomKind.Entrance ? 0 : rng.Next(3);
                sp.variant = rng.Next(3);
                sp.density = 0.45f + 0.55f * (float)rng.NextDouble();

                // Işık: giriş holü hep aydınlık; diğerleri ~%40
                if (sp.kind == RoomKind.Entrance) sp.lit = true;
                else if (sp.IsStair) sp.lit = false;
                else if (sp.IsCorridor) sp.lit = true; // koridorlar 12 m'de bir armatürle (bozuk olabilir)
                else if (sp.kind == RoomKind.ControlRoom || sp.kind == RoomKind.RadioHall) sp.lit = rng.NextDouble() < 0.6;
                else sp.lit = rng.NextDouble() < 0.38;
            }
        }

        void CreateSpaceRoots()
        {
            for (int i = 0; i < spaces.Count; i++)
            {
                var sp = spaces[i];
                var go = new GameObject("Alan " + sp.id + " (" + sp.kind + ")");
                var parent = sp.IsStair ? levelRoots[sp.lvLow] : levelRoots[sp.lv];
                go.transform.SetParent(parent, false);
                sp.root = go.transform;
                if (sp.IsStair)
                {
                    var up = new GameObject("Üst kat parçası");
                    up.transform.SetParent(levelRoots[sp.lv], false);
                    sp.rootUp = up.transform;
                }
            }
        }

        // ---------- Noktalar ve birleştirme ----------

        Vector3 CenterOf(Space sp)
        {
            var r = sp.rects[0];
            return new Vector3(r.x + r.w * 0.5f, 0f, r.z + r.d * 0.5f);
        }

        void CollectPoints()
        {
            MimicSpawns.Clear();
            WanderPoints.Clear();
            Rooms.Clear();
            DoorPoints.Clear();
            Vector3 entranceCenter = spaces.Count > 0 ? CenterOf(spaces[0]) : Vector3.zero;

            for (int i = 0; i < spaces.Count; i++)
            {
                var sp = spaces[i];
                int li = sp.IsStair ? sp.lvLow : sp.lv;
                float baseY = LevelBase[li];

                // Sınır: tüm dikdörtgenlerin birleşimi
                var r0 = sp.rects[0];
                float minX = r0.x, maxX = r0.x + r0.w, minZ = r0.z, maxZ = r0.z + r0.d;
                for (int k = 1; k < sp.rects.Count; k++)
                {
                    var r = sp.rects[k];
                    minX = Mathf.Min(minX, r.x); maxX = Mathf.Max(maxX, r.x + r.w);
                    minZ = Mathf.Min(minZ, r.z); maxZ = Mathf.Max(maxZ, r.z + r.d);
                }
                float height = sp.IsStair ? StepH + RoomH : sp.h;
                var lc = new Vector3((minX + maxX) * 0.5f, baseY + (height - 0.5f) * 0.5f - 0.25f, (minZ + maxZ) * 0.5f);
                var info = new RoomInfo
                {
                    kind = sp.kind,
                    theme = ThemeName(sp.kind),
                    center = genRoot.transform.TransformPoint(new Vector3(lc.x, baseY + 0.05f, lc.z)),
                    bounds = new Bounds(genRoot.transform.TransformPoint(lc), new Vector3(maxX - minX, height + 0.5f, maxZ - minZ)),
                    dark = !sp.lit || sp.IsCorridor || sp.IsStair,
                    corridor = sp.IsCorridor,
                    large = sp.h >= HallH - 0.01f,
                    level = li
                };
                Rooms.Add(info);
                if (sp.IsStair) continue;

                // Gezinti noktaları: oda merkezi; koridorlarda her ~8 m
                if (sp.IsCorridor)
                {
                    CorridorRange(sp, out int cs, out int ce);
                    bool alongZ = AlongZ(sp);
                    for (float u = cs + 2f; u < ce - 1f; u += 8f)
                    {
                        var lp = alongZ ? new Vector3(r0.x + 1.5f, baseY + 0.05f, u) : new Vector3(u, baseY + 0.05f, r0.z + 1.5f);
                        var wp = genRoot.transform.TransformPoint(lp);
                        WanderPoints.Add(wp);
                        if (Vector3.Distance(lp, new Vector3(entranceCenter.x, lp.y, entranceCenter.z)) >= 14f || li != EntLv) MimicSpawns.Add(wp);
                    }
                }
                else
                {
                    var wp = genRoot.transform.TransformPoint(new Vector3(CenterOf(sp).x, baseY + 0.05f, CenterOf(sp).z));
                    WanderPoints.Add(wp);
                    if (sp.kind != RoomKind.Entrance &&
                        (li != EntLv || Vector3.Distance(CenterOf(sp), entranceCenter) >= 14f))
                        MimicSpawns.Add(wp);
                }
            }

            for (int i = 0; i < doors.Count; i++)
            {
                var d = doors[i];
                if (d.kind == 2) continue;
                float cx = d.alongX ? d.start + d.n * 0.5f : d.line;
                float cz = d.alongX ? d.line : d.start + d.n * 0.5f;
                DoorPoints.Add(genRoot.transform.TransformPoint(new Vector3(cx, LevelBase[d.lv] + 0.05f, cz)));
            }
            if (MimicSpawns.Count == 0 && WanderPoints.Count > 1) MimicSpawns.Add(WanderPoints[WanderPoints.Count - 1]);
        }

        static string ThemeName(RoomKind k)
        {
            switch (k)
            {
                case RoomKind.Entrance: return "Giriş Holü";
                case RoomKind.Corridor: return "Koridor";
                case RoomKind.Stair: return "Merdiven";
                case RoomKind.ControlRoom: return "Kontrol Odası";
                case RoomKind.RadioHall: return "Anten Ekipman Salonu";
                case RoomKind.RadioRoom: return "Telsiz Odası";
                case RoomKind.Warehouse: return "Depo Salonu";
                case RoomKind.Storage: return "Depo";
                case RoomKind.Office: return "Ofis";
                case RoomKind.Bunk: return "Koğuş";
                case RoomKind.Generator: return "Jeneratör Odası";
                case RoomKind.Locker: return "Soyunma Odası";
                case RoomKind.Workshop: return "Atölye";
                case RoomKind.Closet: return "Kiler";
                case RoomKind.PipeRoom: return "Boru Odası";
                case RoomKind.Mess: return "Yemekhane";
                default: return k.ToString();
            }
        }

        // Her alanın statik geometrisini toplu çizim için birleştir (hata olursa sessizce atla)
        void CombineStatic()
        {
            try
            {
                for (int i = 0; i < Levels; i++)
                {
                    if (wallRoots[i] != null) StaticBatchingUtility.Combine(wallRoots[i].gameObject);
                }
                foreach (var sp in spaces)
                {
                    if (sp.root != null) StaticBatchingUtility.Combine(sp.root.gameObject);
                    if (sp.rootUp != null) StaticBatchingUtility.Combine(sp.rootUp.gameObject);
                }
            }
            catch (System.Exception e) { Debug.LogWarning("[Tesis] Statik birleştirme atlandı: " + e.Message); }
        }

        void RebuildNavMesh()
        {
            var baker = FindFirstObjectByType<RuntimeNavMeshBaker>();
            NavMeshSurface surface = baker != null ? baker.surface : FindFirstObjectByType<NavMeshSurface>();
            if (surface == null) { Debug.LogWarning("[Tesis] NavMeshSurface bulunamadı; NavMesh yenilenemedi."); return; }
            surface.collectObjects = CollectObjects.All; // iç mekân da dahil
            surface.BuildNavMesh();
            Debug.Log("[Tesis] NavMesh yenilendi.");
        }

        // ---------- Ganimet (sunucu) ----------

        void SpawnLoot()
        {
            if (lootTable == null) { Debug.LogWarning("[Tesis] Ganimet tablosu atanmamış."); return; }
            if (lootPoints.Count == 0) return;

            var rng = new System.Random(generatedSeed ^ 0x5F3759DF);
            int want = Mathf.Min(rng.Next(lootMin, lootMax + 1), lootPoints.Count);
            var pts = new List<Vector3>(lootPoints);
            int spawned = 0;
            for (int i = 0; i < want; i++)
            {
                int j = i + rng.Next(pts.Count - i);
                (pts[i], pts[j]) = (pts[j], pts[i]);

                var prefab = lootTable.Pick(rng);
                if (prefab == null) continue;
                Vector3 pos = genRoot.transform.TransformPoint(pts[i]);
                var go = Instantiate(prefab, pos, Quaternion.Euler(0f, (float)rng.NextDouble() * 360f, 0f));
                var no = go.GetComponent<NetworkObject>();
                if (no == null) { Destroy(go); continue; }
                no.Spawn(true);
                spawned++;
            }
            Debug.Log("[Tesis] " + spawned + " ganimet doğdu.");
        }
    }
}
