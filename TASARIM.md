# "Not Me" — Oyun Tasarımı ve Yol Haritası

## Context
Kullanıcı taklitçinin gerçek bir oyuncudan ayırt edilemeyecek kadar inandırıcı olmasını (hareketleri birebir taklit etmesini) istedi ve oyuna eklenebilecek her şeyin sorulmasını istedi. ~25 soruya verdiği cevaplarla oyunun tamamı netleşti. Oyunun adı **Not Me**. İlk aşama **taklitçi taklidi**; sonra dükkân/fener eşyası, gün/kota, tehlikeler, isim tanıma ve ekstralar gelir. Her aşama Sonnet ajanlarıyla yazdırılır, bilinen hata türleri için taranır, commit+push edilir; kullanıcı Unity'de test eder.

Mevcut temel: `MimicAI` (Explore/Group/Idle/Lure + saldırı, kimlik kuralları), `MovementProfile`/`MovementProfiler` (7 ortalama), `MimicDirector` (zamanla artan taklitçi), `ClipRecorder`/`VoiceClipLibrary` (çağrı/cevap klipleri), `PlayerInventory` (4 slot, server yetkili), `Flashlight` (oyuncuya gömülü), `ChargingStation`, `FacilityGenerator` (4 kat, tohumlu), `LootZone`, `PlayerHealth`/`SpectatorCamera`, Steam lobi/ses.

## Kullanıcı kararları
**Taklitçi**
- Gerçek ganimet almaz; elinde **sahte görsel** taşır. **Hiç kusur yok.**
- Öldürünce **cesedi saklar ve ölünün kılığına girer**.
- Silahla öldürülünce **gerçek yüzünü gösterip ölür**.
- **Kapı açıp kapatabilir**, **ışıkları söndürebilir** (oda/kat).
- **Telsizden konuşabilir.**
- Kılık kuralı (kullanıcının tarifi): Yan yana iki kişi varken toplam 3–4 kişiyse **o ikisinin dışındaki** (3. veya 4.) bir kişinin kılığına girer; birden fazla "diğer" varsa aralarından rastgele. O ikisinden başka kimse yoksa ikisinden birinin kılığına girer (yaklaştığı kişinin kendisi değil). Başka bir gruba yaklaşırken kılığını **oyuncuların görmediği bir yerde** o gruba uygun kişiye değiştirir.

**Eşya / ekonomi**
- Fener **eşya**, **sadece elde seçiliyken** yanar; **ucuz** (zayıf, kısa pil) ve **pahalı** (güçlü, uzun pil) iki tip; başta kimsede yok.
- Dükkân: fenerler, **telsiz**, **silah (levye/kürek)**, **ışık çubuğu/fişek**. Kalıcı yükseltme yok.
- Terminal: araçta, **tıklanan menü** (dükkân / harita / tesis seçimi).
- Başlangıç parası: **iki ucuz fenerlik**. Ortak kasa.
- Ganimet satışı: **günlük piyasa** (teslim makinesi her gün farklı oranda öder).
- Silah: **3 darbede** canavarı da arkadaşı da öldürür.

**Döngü**
- **3 günde bir kota**, her seferinde artar; dolmazsa oyun biter.
- Gün **~18 dakika**; gece ilerledikçe zorlaşır (kararma, sis, daha çok taklitçi/canavar).
- Ölen **gün sonuna kadar izler**, ertesi gün araçta doğar.
- Tesisler: **Radyo istasyonu** (kolay) ve **Eski hastane** (zor); terminalden seçilir, araç **sabit üs**.
- En fazla **4 oyuncu**. İlerleme **kota dönemleri arasında kaydedilir** (host).
- **Can (100) + düşme hasarı**, zamanla yavaş dolar.

**Tehlikeler**
- **Sessiz bekçi:** bakılırken donar, bakılmayınca/karanlıkta yaklaşır.
- **Kör sürünen:** sese göre avlanır (ses, ayak sesi, alarm).
- **Tuzaklar:** çöken zemin, kapanan güvenlik kapıları, alarm.

**Arayüz / sosyal**
- Mikrofon modu ayarlardan: **sürekli açık + M** (varsayılan) / bas-konuş / kapalı.
- Ana menü: **logo + arka plan resmi** (Assets/ThirdParty/Art), **lobi bekleme alanı** (araçta, host "Başla"), **takma ad girme**, **lobide aksesuar seçimi** (şapkalar, gözlük/maske, sırt çantası/atkı; taklitçi kopyalar).
- **Ayarlar menüsü**, **terminal haritası**, **hareketler**: el sallama, işaret etme, dans, baş sallama (taklitçi kaydeder ve tekrarlar).
- **Cesetler** yerde kalır (taklitçi sakladığında kaybolur). **Steam başarımları** (kendi AppID'den sonra).
- Dil: **Türkçe + İngilizce**. Öğretici: **yok**.

**Ek kararlar (2. tur)**
- Envanter: **4 slot + iki elle taşınan büyük eşyalar** (akü, osiloskop vb.; taşırken fener/silah kullanılamaz).
- Taklitçi **nadiren, emin olunca saldırır**; çoğunlukla kandırır ve aranıza karışır (mevcut saldırı tetikleyicileri sıkılaştırılır: daha uzun bekleme, yalnızlık + güven süresi şartı).
- Ölüler: **birbiriyle konuşur** (yaşayanlar duymaz) + yaşayanlara **kısıtlı yazılı ipucu** (önceden tanımlı tek kelimelik işaretler; taklitçiyi doğrudan söyleyemez).
- **Tek zorluk.**
- Telsiz: **elde seçiliyken bas-konuş, pilli**, şarj edilir; telsizi olan herkes cızırtılı duyar.
- Eski hastane: **uzun simetrik koridorlar**, **hasta odaları + ameliyathane**, **güçlü yankı** (fayans), **bodrum morg katı** (en değerli ganimet, en tehlikeli).
- **Müzik yok**, sadece ortam sesi (uğultu, boru tıkırtısı, rüzgâr).
- Kör sürünen yakalarsa **ağır hasar** (canın yarısı).
- Gece: gece yarısına doğru **dışarısı da tehlikeli** (taklitçiler dışarıda avlanır, araç güvenli değil).
- **Oyun sonu ekranı:** "İşte taklitçiler" (her taklitçi kimi taklit etti, kaç kişiyi kandırdı) + unvanlar: En çalışkan, En çok kandırılan, Dost ateşi şampiyonu, En çok taklit edilen, Korkak, Gevezelik kralı, Taklitçi avcısı; ayrıca önerilen ekler: Yalnız kurt (en uzun yalnız kalan), Kâşif (en çok oda gezen), Karanlıkçı (en uzun ışıksız dolaşan), Panikçi (en çok koşan), Yanlış alarm (arkadaşını taklitçi sanıp vuran), Son hayatta kalan, Eli boş (hiç ganimet getirmeyen), Sessiz (en az konuşan). İstatistikler server'da `StatsTracker` ile toplanır.

## Aşama A — Taklitçi taklidi (şimdi)
1. **Davranış kaydı** — yeni `Scripts/Mimic/BehaviorRecorder.cs` (server, `MovementProfiler`'ın yerini alır/genişletir): 10 Hz'de konum, yaw, `PlayerNetwork.pitch`, eğilme, koşma, zıplama, seçili slot, fener, emote olayları; 4–8 sn'lik parçalar, bağlam etiketi (Yürüyüş/EtrafaBakma/GruptaBekleme/Kapı/Merdiven/Kaçış — `FacilityGenerator.Rooms/DoorPoints` + yakın oyuncu sayısı). Oyuncu başına ~60 parça; ölülerinki saklanır.
2. **Zengin profil** — `MovementProfile`'a dağılımlar: bakış dönme hızı/titreşimi, geriye bakma, zıplama, eğilerek yürüme, koşu süresi, gruptaki mesafe/konum, konuşma sıklığı. `MovementProfile.Get` korunur.
3. **Tekrar oynatma** — yeni `Scripts/Mimic/BehaviorPlayback.cs`; `MimicAI.UpdateLookAround/UpdateGroup/UpdateWalk` kimliğin uygun parçasını oynatır (bakış/eğilme/fener/zıplama birebir; yol `NavMesh.SamplePosition` ile uyarlanır, olmazsa sadece bakış kanalları). Grupta grubun baktığı yere bakar.
4. **Yeni kılık kuralı** (yukarıdaki tarif) `MimicAI.ResolveIdentity`'de; değişim sadece görülmezken (`IsSeen`).
5. **Öldürünce kılık değiştirme** — `ServerKill` sonrası taklitçi kimliği ölüye geçer (görülmüyorsa hemen, görülüyorsa ilk fırsatta); ceset sistemi Aşama F'de gelince cesedi saklar.
6. **Sahte eşya** — `NetworkVariable<int> fakeHeldItem` (LootTable indeksi, -1 boş), elde yalnızca görsel kopya; LootZone etkilenmez.
7. **Kapı ve ışık** — FacilityGenerator iç kapıları NetworkObject `Door` olarak üretilir (şu an sadece boşluk); taklitçi geçerken açar/kapatır, ara sıra yalnız oyuncunun odasının ışıklarını söndürür (server → ışık grubu NetworkVariable).
8. **Ayak sesleri** — `Footsteps` bileşeni oyuncu ve taklitçi prefabında aynı (prosedürel, zemine göre beton/metal).

## Aşama B — Eşyalar, dükkân, terminal
Fener eşyaya dönüşür (`FlashlightItem`, iki tip, pil eşyada NetworkVariable, sadece elde seçiliyken yanar; `ChargingStation` eldeki feneri şarj eder; HUD seçili fenerin pili). `IUsableItem` (sol tık). Telsiz (bas-konuş tuşu, telsiz efekti, taklitçi kimlik klibini telsizden çalar), silah (3 darbe; `PlayerHealth`'e can), ışık çubuğu/fişek. Araç terminali: tıklanan menü (Dükkân/Harita/Tesis), ortak kasa, başlangıç parası 2 ucuz fener.

## Aşama C — Gün/kota, tesisler, kayıt
`GameLoop` (server): 18 dk gün, gökyüzü/sis ilerler, gece zorlaşır; terminalden "ayrıl"; içeride kalan ölür; ölüler ertesi gün araçta. Teslim makinesi + günlük piyasa oranı; 3 günde bir artan kota; başarısızlıkta oyun sonu. `FacilityGenerator` tema profilleri: Radyo istasyonu, Eski hastane (oda türleri, malzeme, ganimet tablosu, tehlike yoğunluğu). Host kaydı (para, gün, kota, eşyalar) JSON.

## Aşama D — Tehlikeler
Sessiz bekçi (bakış kontrolü), kör sürünen (`SpeechTracker` + ayak sesleri + alarm), tuzaklar (çöken zemin → alt kat + düşme hasarı, kapanan güvenlik kapıları, alarm). Can sistemi + düşme hasarı.

## Aşama E — İsimleri tanıma
whisper.unity; lobide takma ad; isim içeren çağrı klipleri etiketlenir; taklitçi isimle çağırır.

## Aşama F — Arayüz ve sosyal
Ana menü (logo/arka plan), lobi (araçta bekleme, host Başla), takma ad, aksesuarlar (lobide; taklitçi kopyalar), ayarlar (hassasiyet, ses, mikrofon modu, grafik, dil TR/EN), terminal haritası, emote'lar (el sallama/işaret/dans/baş sallama), cesetler, Steam başarımları, oyun adı **Not Me** (productName, logo).

## Kritik dosyalar
`Scripts/Mimic/MimicAI.cs`, `MovementProfile.cs`, `MimicDirector.cs`; `Scripts/Network/PlayerNetwork.cs`; `Scripts/Level/FacilityGenerator*.cs`, `LootTable.cs`; `Scripts/Environment/Door.cs`; `Scripts/Items/Item.cs`; `Scripts/Player/*`; `Scripts/Voice/*`; `Editor/TestSceneBuilder.cs`.

## Doğrulama
1. Her aşamada: ajan çıktısı tarama betiğiyle (eksik using, `Environment`, `Random` belirsizliği, parantez dengesi, .meta) kontrol → commit → push.
2. Kullanıcı: `git pull` → derleme temiz → MimicGame → Test Sahnesini Oluştur → Multiplayer Play Mode (Yerel IP).
3. Aşama A: Player 2 belirgin alışkanlıklarla (sık zıplama, eğilerek yürüme, sürekli etrafa bakma) 2–3 dk oynar; F9'da kayıtlı/oynatılan parça; taklitçinin bu alışkanlıkları ve kılık kuralını uyguladığı gözlenir; `[Davranış]` / `[Taklitçi]` logları.
