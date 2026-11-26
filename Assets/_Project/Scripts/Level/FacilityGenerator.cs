using System.Collections.Generic;
using MimicGame.Environment;
using MimicGame.Mimic;
using Unity.AI.Navigation;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MimicGame.Level
{
    public enum RoomKind { Entrance, Corridor, Small, Medium, Hall, Storage }

    /// <summary>
    /// Tesisin iç mekânını tohumdan (seed) üretir. Sunucu tohumu seçer; her istemci aynı yerleşimi
    /// yalnızca System.Random(seed) ile ilkel şekillerden kurar. İç mekân haritanın çok altındadır (origin).
    /// Sunucu üretimden sonra NavMesh'i yeniler, ganimetleri ağ nesnesi olarak doğurur.
    /// </summary>
    public class FacilityGenerator : NetworkBehaviour
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
        /// <summary>Dünya konumları: tüm oda merkezleri (taklitçi gezintisi için).</summary>
        public static readonly List<Vector3> WanderPoints = new List<Vector3>();

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

        // ---------- Ayarlar ----------

        [Header("Dışarı çıkış noktası")]
        public Vector3 outsideExitPoint = new Vector3(0f, 0.05f, 7.5f);
        public float outsideExitYaw = 180f;

        [Header("İçerik")]
        public LootTable lootTable;
        public int minRooms = 14;
        public int maxRooms = 20;
        public int minLoot = 14;
        public int maxLoot = 22;

        [Header("Malzemeler (builder atar; boşsa çalışma anında üretilir)")]
        public Material concrete;
        public Material wallMat;
        public Material ceilingMat;
        public Material lineMat;
        public Material metalMat;
        public Material crateMat;
        public Material deskMat;
        public Material doorFrameMat;
        public Material doorMat;
        public Material screenMat;
        public Material stripOnMat;
        public Material stripOffMat;
        public Material ledGreenMat;
        public Material ledRedMat;
        public Material exitSignMat;
        public Material pipeMat;

        public Vector3 ArrivalPoint => genRoot != null ? genRoot.transform.TransformPoint(arrivalLocal) : transform.position;
        public float ArrivalYaw => 0f;
        public Vector3 OutsideExitPoint => outsideExitPoint;
        public float OutsideExitYaw => outsideExitYaw;
        public int Seed => seed.Value;
        public int RoomCount => rooms.Count;
        public int LootPointCount => lootPoints.Count;

        // ---------- Sabitler ----------

        const float Cell = 4f;
        const float WallH = 4f;
        const float WallT = 0.3f;
        const float DoorW = 2f;
        const float DoorH = 2.6f;

        static readonly int[] DX = { 0, 1, 0, -1 };
        static readonly int[] DZ = { 1, 0, -1, 0 };

        sealed class Room
        {
            public RoomKind kind;
            public int x, z, w, d;
            public bool dark;
            public Transform root;
            public Vector3 center;
            public int Cells => w * d;
        }

        struct Slot
        {
            public Room room;
            public int x, z, dir;
        }

        sealed class Spot
        {
            public Vector3 pos;
            public Vector3 inward;
            public bool corner;
        }

        // Sunucu yazar, herkes okur
        readonly NetworkVariable<int> seed = new NetworkVariable<int>(0,
            NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        readonly List<Room> rooms = new List<Room>();
        readonly Dictionary<Vector2Int, Room> cellRoom = new Dictionary<Vector2Int, Room>();
        readonly HashSet<Vector3Int> openEdges = new HashSet<Vector3Int>();
        readonly List<Slot> slots = new List<Slot>();
        readonly List<Vector3> lootPoints = new List<Vector3>(); // üretici-yerel konumlar
        readonly Vector3Int exitEdge = new Vector3Int(1, 0, 0);  // giriş holünün güney orta kenarı

        GameObject genRoot;
        Vector3 arrivalLocal = new Vector3(6f, 0.05f, 2.2f);
        int generatedSeed;
        bool lootSpawned;

        static Mesh cubeMesh, cylinderMesh;

        // ---------- Ağ yaşam döngüsü ----------

        public override void OnNetworkSpawn()
        {
            Instance = this;
            seed.OnValueChanged += OnSeedChanged;
            if (IsServer) seed.Value = Random.Range(1, int.MaxValue); // her sunucu başlangıcında yeni tohum
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
                Debug.Log("[Tesis] Tohum " + seed.Value + ", " + rooms.Count + " oda, " + lootPoints.Count + " ganimet noktası");
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

            BuildLayout(rng);
            foreach (var r in rooms)
                if (r.kind != RoomKind.Entrance) r.dark = rng.NextDouble() < 0.3;

            foreach (var r in rooms) BuildRoomShell(r, rng);

            // Duvarlar (her kenar bir kez)
            var built = new HashSet<Vector3Int>();
            foreach (var r in rooms) BuildRoomWalls(r, built);

            foreach (var r in rooms)
            {
                DecorateRoom(r, rng);
                AddRoomLights(r, rng);
            }

            CollectPoints();
            CombineStatic();
            Physics.SyncTransforms();

            if (IsServer) RebuildNavMesh();

            Generated = true;
            OnGenerated?.Invoke();
            Debug.Log("[Tesis] Oluşturuldu: tohum " + s + ", " + rooms.Count + " oda, " + lootPoints.Count + " ganimet noktası");
        }

        void ClearGenerated()
        {
            Generated = false;
            lootSpawned = false;
            generatedSeed = 0;
            MimicSpawns.Clear();
            WanderPoints.Clear();
            rooms.Clear();
            cellRoom.Clear();
            openEdges.Clear();
            slots.Clear();
            lootPoints.Clear();
            if (genRoot != null) Destroy(genRoot);
            genRoot = null;
        }

        // ---------- Yerleşim ----------

        void BuildLayout(System.Random rng)
        {
            var entrance = AddRoom(RoomKind.Entrance, 0, 0, 3, 3);
            AddSlots(entrance);

            int target = rng.Next(minRooms, maxRooms + 1);
            int halls = 0;
            for (int attempt = 0; attempt < 200 && rooms.Count < target && slots.Count > 0; attempt++)
            {
                int si = rng.Next(slots.Count);
                var s = slots[si];
                int nx = s.x + DX[s.dir], nz = s.z + DZ[s.dir];

                if (cellRoom.TryGetValue(new Vector2Int(nx, nz), out var neighbor))
                {
                    // Komşu zaten oda: ara sıra döngü oluşturmak için kapıyı aç
                    if (neighbor != s.room && Edge(s.x, s.z, s.dir) != exitEdge && rng.NextDouble() < 0.15)
                        openEdges.Add(Edge(s.x, s.z, s.dir));
                    RemoveSlot(si);
                    continue;
                }

                var kind = PickKind(rng);
                if (kind == RoomKind.Hall && halls >= 2) kind = RoomKind.Medium;
                SizeOf(kind, rng, out int w, out int d);
                if (!TryFit(s, nx, nz, w, d, rng, out int x0, out int z0)) continue;

                var room = AddRoom(kind, x0, z0, w, d);
                if (kind == RoomKind.Hall) halls++;
                openEdges.Add(Edge(s.x, s.z, s.dir));
                RemoveSlot(si);
                AddSlots(room);
            }
        }

        Room AddRoom(RoomKind kind, int x, int z, int w, int d)
        {
            var r = new Room { kind = kind, x = x, z = z, w = w, d = d };
            r.center = new Vector3((x + w * 0.5f) * Cell, 0f, (z + d * 0.5f) * Cell);
            for (int i = 0; i < w; i++)
                for (int j = 0; j < d; j++)
                    cellRoom[new Vector2Int(x + i, z + j)] = r;
            rooms.Add(r);
            return r;
        }

        void AddSlots(Room r)
        {
            for (int cx = r.x; cx < r.x + r.w; cx++)
                for (int cz = r.z; cz < r.z + r.d; cz++)
                    for (int dir = 0; dir < 4; dir++)
                    {
                        int nx = cx + DX[dir], nz = cz + DZ[dir];
                        if (nx >= r.x && nx < r.x + r.w && nz >= r.z && nz < r.z + r.d) continue; // oda içi
                        if (Edge(cx, cz, dir) == exitEdge && r.kind == RoomKind.Entrance) continue; // çıkış kapısı
                        slots.Add(new Slot { room = r, x = cx, z = cz, dir = dir });
                    }
        }

        void RemoveSlot(int index)
        {
            int last = slots.Count - 1;
            slots[index] = slots[last];
            slots.RemoveAt(last);
        }

        static RoomKind PickKind(System.Random rng)
        {
            int r = rng.Next(100);
            if (r < 30) return RoomKind.Corridor;
            if (r < 50) return RoomKind.Small;
            if (r < 68) return RoomKind.Medium;
            if (r < 82) return RoomKind.Storage;
            return RoomKind.Hall;
        }

        static void SizeOf(RoomKind kind, System.Random rng, out int w, out int d)
        {
            switch (kind)
            {
                case RoomKind.Corridor: w = 1; d = 3; break;
                case RoomKind.Small: w = 2; d = 2; break;
                case RoomKind.Medium: w = 3; d = 3; break;
                case RoomKind.Hall: w = 4; d = 5; break;
                case RoomKind.Storage: w = 2; d = 3; break;
                default: w = 3; d = 3; break;
            }
            if (rng.Next(2) == 0) { int t = w; w = d; d = t; } // yön
        }

        bool TryFit(Slot s, int nx, int nz, int w, int d, System.Random rng, out int x0, out int z0)
        {
            x0 = 0; z0 = 0;
            int span = (s.dir == 0 || s.dir == 2) ? w : d;
            int start = rng.Next(span);
            for (int k = 0; k < span; k++)
            {
                int off = (start + k) % span;
                switch (s.dir)
                {
                    case 0: x0 = nx - off; z0 = nz; break;
                    case 2: x0 = nx - off; z0 = nz - d + 1; break;
                    case 1: x0 = nx; z0 = nz - off; break;
                    default: x0 = nx - w + 1; z0 = nz - off; break;
                }
                if (RectFree(x0, z0, w, d)) return true;
            }
            return false;
        }

        bool RectFree(int x0, int z0, int w, int d)
        {
            for (int i = 0; i < w; i++)
                for (int j = 0; j < d; j++)
                    if (cellRoom.ContainsKey(new Vector2Int(x0 + i, z0 + j))) return false;
            return true;
        }

        // Hücre kenarı kimliği: (x, z, eksen). Eksen 0: X boyunca duvar (z çizgisi), 1: Z boyunca duvar (x çizgisi)
        static Vector3Int Edge(int x, int z, int dir)
        {
            switch (dir)
            {
                case 0: return new Vector3Int(x, z + 1, 0);
                case 1: return new Vector3Int(x + 1, z, 1);
                case 2: return new Vector3Int(x, z, 0);
                default: return new Vector3Int(x, z, 1);
            }
        }

        // ---------- Kabuk: zemin, tavan, duvarlar ----------

        void BuildRoomShell(Room r, System.Random rng)
        {
            var go = new GameObject("Oda " + rooms.IndexOf(r) + " (" + r.kind + ")");
            go.transform.SetParent(genRoot.transform, false);
            r.root = go.transform;

            float sx = r.w * Cell, sz = r.d * Cell;
            Block(r.root, "Zemin", new Vector3(r.center.x, -0.1f, r.center.z), new Vector3(sx, 0.2f, sz), concrete);
            var ceil = Block(r.root, "Tavan", new Vector3(r.center.x, WallH + 0.1f, r.center.z),
                new Vector3(sx, 0.2f, sz), ceilingMat);
            ceil.AddComponent<NavMeshModifier>().ignoreFromBuild = true; // tavanın üstü yürünebilir sayılmasın
        }

        void BuildRoomWalls(Room r, HashSet<Vector3Int> built)
        {
            for (int cx = r.x; cx < r.x + r.w; cx++)
                for (int cz = r.z; cz < r.z + r.d; cz++)
                    for (int dir = 0; dir < 4; dir++)
                    {
                        cellRoom.TryGetValue(new Vector2Int(cx + DX[dir], cz + DZ[dir]), out var other);
                        if (other == r) continue;
                        var e = Edge(cx, cz, dir);
                        if (!built.Add(e)) continue;
                        bool open = other != null && openEdges.Contains(e);
                        BuildWall(r.root, e, open);
                        if (r.kind == RoomKind.Entrance && e == exitEdge) BuildExitDoor(r.root, e);
                    }
        }

        void BuildWall(Transform parent, Vector3Int e, bool open)
        {
            bool alongX = e.z == 0;
            Vector3 c = alongX
                ? new Vector3((e.x + 0.5f) * Cell, 0f, e.y * Cell)
                : new Vector3(e.x * Cell, 0f, (e.y + 0.5f) * Cell);
            Vector3 along = alongX ? Vector3.right : Vector3.forward;

            if (!open)
            {
                Block(parent, "Duvar", c + Vector3.up * (WallH * 0.5f), WallSize(alongX, Cell + WallT, WallH), wallMat);
                return;
            }

            // Kapı boşluğu: iki yan parça + lento
            float half = DoorW * 0.5f;
            float segLen = (Cell * 0.5f + WallT * 0.5f) - half;
            float segC = half + segLen * 0.5f;
            Block(parent, "Duvar (sol)", c + along * -segC + Vector3.up * (WallH * 0.5f), WallSize(alongX, segLen, WallH), wallMat);
            Block(parent, "Duvar (sağ)", c + along * segC + Vector3.up * (WallH * 0.5f), WallSize(alongX, segLen, WallH), wallMat);
            Block(parent, "Kapı Üstü", c + Vector3.up * (DoorH + (WallH - DoorH) * 0.5f),
                WallSize(alongX, DoorW, WallH - DoorH), wallMat);

            // Kapı çerçevesi (çarpıştırıcısız, yalnızca görsel)
            float depth = WallT + 0.08f;
            Vector3 post = alongX ? new Vector3(0.1f, DoorH, depth) : new Vector3(depth, DoorH, 0.1f);
            Block(parent, "Çerçeve", c + along * (half + 0.05f) + Vector3.up * (DoorH * 0.5f), post, doorFrameMat, false);
            Block(parent, "Çerçeve", c + along * -(half + 0.05f) + Vector3.up * (DoorH * 0.5f), post, doorFrameMat, false);
            Vector3 beam = alongX ? new Vector3(DoorW + 0.2f, 0.1f, depth) : new Vector3(depth, 0.1f, DoorW + 0.2f);
            Block(parent, "Çerçeve", c + Vector3.up * (DoorH + 0.05f), beam, doorFrameMat, false);
        }

        static Vector3 WallSize(bool alongX, float length, float height)
            => alongX ? new Vector3(length, height, WallT) : new Vector3(WallT, height, length);

        // Giriş holündeki çıkış kapısı: duvarın önünde etkileşimli panel + yeşil çıkış levhası
        void BuildExitDoor(Transform parent, Vector3Int e)
        {
            Vector3 c = new Vector3((e.x + 0.5f) * Cell, 0f, e.y * Cell); // güney duvarı (eksen X)
            var panel = Block(parent, "Çıkış Kapısı", c + new Vector3(0f, 1.25f, 0.3f), new Vector3(1.8f, 2.5f, 0.1f), doorMat);
            var door = panel.AddComponent<FacilityDoor>();
            door.isExit = true;
            Block(parent, "Çıkış Levhası", c + new Vector3(0f, 2.85f, 0.25f), new Vector3(1.0f, 0.22f, 0.06f), exitSignMat, false);
            Block(parent, "Çıkış Çerçevesi", c + new Vector3(0f, 1.3f, 0.22f), new Vector3(2.0f, 2.7f, 0.06f), doorFrameMat, false);
        }

        // ---------- Işıklar ----------

        void AddRoomLights(Room r, System.Random rng)
        {
            var cells = new List<Vector3>();
            for (int cx = r.x; cx < r.x + r.w; cx++)
                for (int cz = r.z; cz < r.z + r.d; cz++)
                    cells.Add(new Vector3((cx + 0.5f) * Cell, 0f, (cz + 0.5f) * Cell));

            // Floresan şeritler (karanlık odada sönük)
            float yaw = r.w >= r.d ? 0f : 90f;
            var stripMat = r.dark ? stripOffMat : stripOnMat;
            foreach (var c in cells)
                Block(r.root, "Floresan", new Vector3(c.x, WallH - 0.06f, c.z), new Vector3(1.6f, 0.06f, 0.3f), stripMat, false, yaw);

            if (r.dark) return;
            int n = Mathf.Clamp((cells.Count + 3) / 4, 1, 5);
            for (int i = 0; i < n; i++)
            {
                int idx = Mathf.Min(cells.Count - 1, (int)((i + 0.5f) * cells.Count / n));
                var c = cells[idx];
                AddLight(r.root, new Vector3(c.x, WallH - 0.5f, c.z), r.kind == RoomKind.Entrance && i == 0, rng);
            }
        }

        void AddLight(Transform parent, Vector3 pos, bool shadows, System.Random rng)
        {
            var go = new GameObject("Floresan Işık");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = new Color(0.85f, 0.95f, 1f);
            l.intensity = 1.2f;
            l.range = 6f;
            l.shadows = shadows ? LightShadows.Soft : LightShadows.None;
            if (rng.NextDouble() < 0.2)
            {
                var f = go.AddComponent<LightFlicker>(); // intensity ayarlandıktan sonra eklenir
                f.flickerChance = 0.08f;
            }
        }

        // ---------- Dekor ----------

        void DecorateRoom(Room r, System.Random rng)
        {
            var edges = new List<Spot>();
            var corners = new List<Spot>();
            var inner = new List<Vector3>();
            CollectSpots(r, edges, corners, inner);

            switch (r.kind)
            {
                case RoomKind.Corridor:
                    DecorCorridor(r, rng);
                    break;

                case RoomKind.Entrance:
                    FloorLine(r.root, new Vector3(r.center.x - 1.1f, 0f, 4.0f), new Vector3(0.14f, 0.015f, 6f));
                    FloorLine(r.root, new Vector3(r.center.x + 1.1f, 0f, 4.0f), new Vector3(0.14f, 0.015f, 6f));
                    FloorLine(r.root, new Vector3(r.center.x, 0f, 1.0f), new Vector3(2.4f, 0.015f, 0.14f));
                    HorizontalPipe(r, rng, 3.7f);
                    var north = edges.FindAll(s0 => s0.inward.z < -0.5f);
                    if (north.Count > 0) PlaceDesk(r.root, Take(north, rng), rng, true);
                    var west = edges.FindAll(s0 => s0.inward.x > 0.5f);
                    if (west.Count > 0) PlaceConsole(r.root, Take(west, rng), rng);
                    if (corners.Count > 0) PlaceCrateOrBarrel(r.root, Take(corners, rng), rng);
                    if (corners.Count > 0) PlaceCabinet(r.root, Take(corners, rng), rng);
                    break;

                case RoomKind.Small:
                    if (edges.Count > 0) PlaceDesk(r.root, Take(edges, rng), rng, true);
                    if (corners.Count > 0) PlaceCabinet(r.root, Take(corners, rng), rng);
                    if (corners.Count > 0) PlaceCrateOrBarrel(r.root, Take(corners, rng), rng);
                    if (corners.Count > 0 && rng.Next(2) == 0) PlaceCrateOrBarrel(r.root, Take(corners, rng), rng);
                    break;

                case RoomKind.Medium:
                    for (int i = 0; i < 3 && edges.Count > 0; i++) PlaceConsole(r.root, Take(edges, rng), rng);
                    if (edges.Count > 0) PlaceDesk(r.root, Take(edges, rng), rng, true);
                    for (int i = 0; i < 2 && inner.Count > 0; i++) Pillar(r.root, TakeV(inner, rng));
                    foreach (var sp in corners) PlaceCrateOrBarrel(r.root, sp, rng);
                    HorizontalPipe(r, rng, 3.65f);
                    break;

                case RoomKind.Hall:
                    // Kontrol odası: duvarlarda konsollar, ortada masalar ve direkler
                    foreach (var sp in edges)
                    {
                        if (rng.NextDouble() < 0.7) PlaceConsole(r.root, sp, rng);
                        else PlaceCabinet(r.root, sp, rng);
                    }
                    foreach (var v in inner)
                    {
                        double q = rng.NextDouble();
                        if (q < 0.4) Pillar(r.root, v);
                        else if (q < 0.7) Desk(r.root, v, rng.Next(2) * 90f, rng, true);
                    }
                    foreach (var sp in corners) PlaceCrateOrBarrel(r.root, sp, rng);
                    FloorLine(r.root, new Vector3(r.center.x, 0f, r.center.z), new Vector3(r.w * Cell - 1.5f, 0.015f, 0.14f));
                    FloorLine(r.root, new Vector3(r.center.x, 0f, r.center.z), new Vector3(0.14f, 0.015f, r.d * Cell - 1.5f));
                    HorizontalPipe(r, rng, 3.7f);
                    HorizontalPipe(r, rng, 3.4f);
                    break;

                case RoomKind.Storage:
                    foreach (var sp in edges)
                        if (rng.NextDouble() < 0.85) Shelf(r.root, sp.pos + sp.inward * 0.45f, YawOf(sp.inward), rng);
                    foreach (var v in inner) Shelf(r.root, v, rng.Next(2) * 90f, rng);
                    foreach (var sp in corners) PlaceCrateOrBarrel(r.root, sp, rng);
                    break;
            }

            // Odanın zemininde birkaç ek ganimet noktası
            if (r.kind != RoomKind.Entrance)
            {
                int extra = 1 + rng.Next(2);
                for (int i = 0; i < extra; i++)
                {
                    float px = r.x * Cell + 1.2f + (float)rng.NextDouble() * (r.w * Cell - 2.4f);
                    float pz = r.z * Cell + 1.2f + (float)rng.NextDouble() * (r.d * Cell - 2.4f);
                    lootPoints.Add(new Vector3(px, 0.05f, pz));
                }
            }
        }

        void DecorCorridor(Room r, System.Random rng)
        {
            bool alongZ = r.d > r.w;
            float len = Mathf.Max(r.w, r.d) * Cell;
            Vector3 c = r.center;
            Vector3 axis = alongZ ? Vector3.forward : Vector3.right;
            Vector3 lat = alongZ ? Vector3.right : Vector3.forward;

            // Sarı güvenlik çizgileri
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 size = alongZ ? new Vector3(0.12f, 0.015f, len - 0.6f) : new Vector3(len - 0.6f, 0.015f, 0.12f);
                FloorLine(r.root, c + lat * (s * 1.5f), size);
            }
            // Tavan boruları
            for (int s = -1; s <= 1; s += 2)
            {
                Vector3 a = c + lat * (s * 1.7f) + Vector3.up * 3.55f - axis * (len * 0.5f);
                Pipe(r.root, a, a + axis * len, 0.16f);
            }
            // Uçta bir sandık ya da varil (kapı ağızlarını kapatmayacak şekilde)
            if (rng.NextDouble() < 0.55)
            {
                float sideSign = rng.Next(2) == 0 ? -1f : 1f;
                float endSign = rng.Next(2) == 0 ? -1f : 1f;
                Vector3 p = c + lat * (sideSign * 1.45f) + axis * (endSign * (len * 0.5f - 0.5f));
                if (rng.Next(2) == 0) Crate(r.root, p, 0.6f, rng.Next(20) - 10f, rng);
                else Barrel(r.root, p);
            }
        }

        void HorizontalPipe(Room r, System.Random rng, float height)
        {
            bool alongX = r.w >= r.d;
            float len = (alongX ? r.w : r.d) * Cell;
            float off = ((float)rng.NextDouble() - 0.5f) * 2f;
            Vector3 a = alongX
                ? new Vector3(r.x * Cell, height, r.center.z + off)
                : new Vector3(r.center.x + off, height, r.z * Cell);
            Pipe(r.root, a, a + (alongX ? Vector3.right : Vector3.forward) * len, 0.2f);
        }

        // Odanın köşe/kenar/iç köşe noktaları (kapılar hücre kenarı ortasında; bu noktalar kapılardan >= 1 m uzakta)
        void CollectSpots(Room r, List<Spot> edges, List<Spot> corners, List<Vector3> inner)
        {
            for (int i = 0; i <= r.w; i++)
                for (int j = 0; j <= r.d; j++)
                {
                    var v = new Vector3((r.x + i) * Cell, 0f, (r.z + j) * Cell);
                    bool bx = i == 0 || i == r.w, bz = j == 0 || j == r.d;
                    if (bx && bz)
                        corners.Add(new Spot { pos = v, inward = new Vector3(i == 0 ? 1f : -1f, 0f, j == 0 ? 1f : -1f), corner = true });
                    else if (bx)
                        edges.Add(new Spot { pos = v, inward = new Vector3(i == 0 ? 1f : -1f, 0f, 0f) });
                    else if (bz)
                        edges.Add(new Spot { pos = v, inward = new Vector3(0f, 0f, j == 0 ? 1f : -1f) });
                    else inner.Add(v);
                }
        }

        static float YawOf(Vector3 inward) => Mathf.Atan2(inward.x, inward.z) * Mathf.Rad2Deg;

        static Spot Take(List<Spot> list, System.Random rng)
        {
            int i = rng.Next(list.Count);
            var s = list[i];
            list.RemoveAt(i);
            return s;
        }

        static Vector3 TakeV(List<Vector3> list, System.Random rng)
        {
            int i = rng.Next(list.Count);
            var v = list[i];
            list.RemoveAt(i);
            return v;
        }

        void PlaceDesk(Transform p, Spot sp, System.Random rng, bool monitor)
            => Desk(p, sp.pos + sp.inward * (0.2f + 0.4f), YawOf(sp.inward), rng, monitor);

        void PlaceConsole(Transform p, Spot sp, System.Random rng)
            => Console(p, sp.pos + sp.inward * (0.2f + 0.35f), YawOf(sp.inward), rng);

        void PlaceCabinet(Transform p, Spot sp, System.Random rng)
        {
            Vector3 pos = sp.pos + (sp.corner ? sp.inward.normalized * 0.8f : sp.inward * 0.45f);
            Cabinet(p, pos, sp.corner ? 0f : YawOf(sp.inward));
        }

        void PlaceCrateOrBarrel(Transform p, Spot sp, System.Random rng)
        {
            Vector3 pos = sp.pos + sp.inward * 0.6f; // köşeden her iki duvardan 0.6 m
            if (rng.Next(2) == 0) Crate(p, pos, 0.7f, rng.Next(16) - 8f, rng);
            else Barrel(p, pos);
        }

        // ---------- Prop'lar ----------

        Transform Prop(Transform parent, string name, Vector3 pos, float yaw)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            return go.transform;
        }

        static Vector3 Local(Vector3 pos, float yaw, Vector3 offset) => pos + Quaternion.Euler(0f, yaw, 0f) * offset;

        void Crate(Transform parent, Vector3 pos, float size, float yaw, System.Random rng)
        {
            float h = size * (0.7f + 0.5f * (float)rng.NextDouble());
            Block(parent, "Sandık", pos + Vector3.up * (h * 0.5f), new Vector3(size, h, size), crateMat, true, yaw);
            lootPoints.Add(pos + Vector3.up * (h + 0.03f));
        }

        void Barrel(Transform parent, Vector3 pos)
        {
            var go = new GameObject("Varil");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos + Vector3.up * 0.45f;
            go.transform.localScale = new Vector3(0.6f, 0.45f, 0.6f);
            go.AddComponent<MeshFilter>().sharedMesh = CylinderMesh();
            go.AddComponent<MeshRenderer>().sharedMaterial = metalMat;
            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 2f, 1f); // silindir ilkeli 1x2x1
        }

        void Pillar(Transform parent, Vector3 pos)
            => Block(parent, "Direk", pos + Vector3.up * (WallH * 0.5f), new Vector3(0.6f, WallH, 0.6f), concrete);

        void Cabinet(Transform parent, Vector3 pos, float yaw)
        {
            var c = Prop(parent, "Dolap", pos, yaw);
            Block(c, "Gövde", new Vector3(0f, 0.95f, 0f), new Vector3(0.9f, 1.9f, 0.5f), metalMat);
            Block(c, "Kol", new Vector3(0.25f, 1.0f, 0.27f), new Vector3(0.05f, 0.3f, 0.04f), doorFrameMat, false);
        }

        void Desk(Transform parent, Vector3 pos, float yaw, System.Random rng, bool monitor)
        {
            var d = Prop(parent, "Masa", pos, yaw);
            Block(d, "Tabla", new Vector3(0f, 0.76f, 0f), new Vector3(1.6f, 0.06f, 0.8f), deskMat);
            Block(d, "Sol Ayak", new Vector3(-0.77f, 0.365f, 0f), new Vector3(0.06f, 0.73f, 0.76f), metalMat);
            Block(d, "Sağ Ayak", new Vector3(0.77f, 0.365f, 0f), new Vector3(0.06f, 0.73f, 0.76f), metalMat);
            if (monitor)
            {
                Block(d, "Monitör Ayağı", new Vector3(0f, 0.85f, -0.2f), new Vector3(0.08f, 0.12f, 0.08f), metalMat, false);
                Block(d, "Monitör", new Vector3(0f, 1.05f, -0.2f), new Vector3(0.5f, 0.33f, 0.05f), screenMat, false);
            }
            float ox = ((float)rng.NextDouble() - 0.5f) * 0.9f;
            lootPoints.Add(Local(pos, yaw, new Vector3(ox, 0.82f, 0.15f)));
        }

        void Console(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            const float w = 1.6f, dp = 0.7f;
            var c = Prop(parent, "Konsol", pos, yaw);
            Block(c, "Gövde", new Vector3(0f, 0.45f, 0f), new Vector3(w, 0.9f, dp), metalMat);
            // Eğik ekran
            var screen = Block(c, "Ekran", new Vector3(0f, 1.25f, -0.18f), new Vector3(w * 0.8f, 0.45f, 0.05f), screenMat, false);
            screen.transform.localRotation = Quaternion.Euler(-20f, 0f, 0f);
            // Gösterge ışıkları
            for (int i = 0; i < 4; i++)
            {
                bool red = rng.NextDouble() < 0.35;
                Block(c, "Gösterge", new Vector3(-w * 0.5f + 0.3f + i * 0.35f, 0.93f, 0.18f),
                    new Vector3(0.08f, 0.04f, 0.08f), red ? ledRedMat : ledGreenMat, false);
            }
        }

        void Shelf(Transform parent, Vector3 pos, float yaw, System.Random rng)
        {
            const float L = 1.8f, D = 0.5f;
            var s = Prop(parent, "Raf", pos, yaw);
            Block(s, "Yan Sol", new Vector3(-(L * 0.5f - 0.025f), 1.0f, 0f), new Vector3(0.05f, 2.0f, D), metalMat);
            Block(s, "Yan Sağ", new Vector3(L * 0.5f - 0.025f, 1.0f, 0f), new Vector3(0.05f, 2.0f, D), metalMat);
            Block(s, "Arka", new Vector3(0f, 1.0f, -(D * 0.5f - 0.015f)), new Vector3(L, 2.0f, 0.03f), metalMat);
            float[] ys = { 0.30f, 0.92f, 1.54f };
            for (int i = 0; i < ys.Length; i++)
            {
                Block(s, "Tabla", new Vector3(0f, ys[i], 0f), new Vector3(L - 0.1f, 0.04f, D), deskMat);
                float ox = ((float)rng.NextDouble() - 0.5f) * (L - 0.7f);
                lootPoints.Add(Local(pos, yaw, new Vector3(ox, ys[i] + 0.05f, 0.02f)));
                // Kutular raflara görsel doluluk katar
                if (rng.NextDouble() < 0.6)
                {
                    float bx = ((float)rng.NextDouble() - 0.5f) * (L - 0.6f);
                    if (Mathf.Abs(bx - ox) > 0.45f)
                        Block(s, "Kutu", new Vector3(bx, ys[i] + 0.02f + 0.12f, 0.02f), new Vector3(0.3f, 0.24f, 0.3f), crateMat);
                }
            }
        }

        void FloorLine(Transform parent, Vector3 center, Vector3 size)
            => Block(parent, "Güvenlik Çizgisi", new Vector3(center.x, size.y * 0.5f + 0.0005f, center.z), size, lineMat, false);

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

        // ---------- Noktalar ve birleştirme ----------

        void CollectPoints()
        {
            MimicSpawns.Clear();
            WanderPoints.Clear();
            Vector3 entranceCenter = rooms.Count > 0 ? rooms[0].center : Vector3.zero;
            Room farthest = null;
            float farD = -1f;
            foreach (var r in rooms)
            {
                Vector3 world = genRoot.transform.TransformPoint(new Vector3(r.center.x, 0.05f, r.center.z));
                WanderPoints.Add(world);
                if (r.kind == RoomKind.Entrance) continue;
                float d = Vector3.Distance(r.center, entranceCenter);
                if (d >= 14f) MimicSpawns.Add(world);
                if (d > farD) { farD = d; farthest = r; }
            }
            if (MimicSpawns.Count == 0 && farthest != null)
                MimicSpawns.Add(genRoot.transform.TransformPoint(new Vector3(farthest.center.x, 0.05f, farthest.center.z)));
        }

        // Her odanın statik geometrisini toplu çizim için birleştir (hata olursa sessizce atla)
        void CombineStatic()
        {
            foreach (var r in rooms)
            {
                if (r.root == null) continue;
                try { StaticBatchingUtility.Combine(r.root.gameObject); }
                catch (System.Exception e) { Debug.LogWarning("[Tesis] Statik birleştirme atlandı: " + e.Message); break; }
            }
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
            int want = Mathf.Min(rng.Next(minLoot, maxLoot + 1), lootPoints.Count);
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

        // Atanmamış malzemeler için çalışma anı yedeği
        void EnsureMaterials()
        {
            Ensure(ref concrete, new Color(0.2f, 0.2f, 0.21f), default);
            Ensure(ref wallMat, new Color(0.3f, 0.26f, 0.22f), default);
            Ensure(ref ceilingMat, new Color(0.12f, 0.12f, 0.13f), default);
            Ensure(ref lineMat, new Color(0.85f, 0.7f, 0.1f), default);
            Ensure(ref metalMat, new Color(0.28f, 0.3f, 0.3f), default);
            Ensure(ref crateMat, new Color(0.35f, 0.26f, 0.15f), default);
            Ensure(ref deskMat, new Color(0.25f, 0.22f, 0.2f), default);
            Ensure(ref doorFrameMat, new Color(0.1f, 0.1f, 0.11f), default);
            Ensure(ref doorMat, new Color(0.15f, 0.25f, 0.2f), default);
            Ensure(ref screenMat, new Color(0.05f, 0.2f, 0.15f), new Color(0.2f, 1.2f, 0.9f));
            Ensure(ref stripOnMat, new Color(0.8f, 0.9f, 1f), new Color(2.2f, 2.4f, 2.6f));
            Ensure(ref stripOffMat, new Color(0.1f, 0.1f, 0.1f), default);
            Ensure(ref ledGreenMat, new Color(0.05f, 0.3f, 0.05f), new Color(0.2f, 2f, 0.3f));
            Ensure(ref ledRedMat, new Color(0.3f, 0.05f, 0.05f), new Color(2f, 0.2f, 0.15f));
            Ensure(ref exitSignMat, new Color(0.05f, 0.3f, 0.1f), new Color(0.2f, 2f, 0.5f));
            Ensure(ref pipeMat, new Color(0.25f, 0.2f, 0.17f), default);
        }

        static void Ensure(ref Material m, Color color, Color emission)
        {
            if (m != null) return;
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null) shader = Shader.Find("Standard");
            m = new Material(shader);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", color);
            else m.color = color;
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.2f);
            if (emission.maxColorComponent > 0f)
            {
                m.EnableKeyword("_EMISSION");
                m.SetColor("_EmissionColor", emission);
            }
        }
    }
}
