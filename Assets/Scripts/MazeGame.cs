// MazeGame.cs — AR-лабиринт (Unity 6 + Vuforia), управление с ПК и телефона.
//
// Повесь этот скрипт на корневой объект лабиринта (ребёнок ImageTarget, в нём стены/пол),
// укажи Ball и Finish. Всё остальное (лунка, надпись WIN, конфетти, звук) создаётся само.
//
// Почему шарик раньше вылетал/проходил сквозь стены:
//   Vuforia каждый кадр "телепортирует" лабиринт за картинкой. Стены прыгают прямо в шарик,
//   физика выталкивает его вверх или по ту сторону стены.
// Как исправлено:
//   Физика шарика считается в невидимой НЕПОДВИЖНОЙ копии лабиринта (коллайдеры стен копируются
//   туда при старте), а видимый шарик просто повторяет её позицию внутри AR-лабиринта.
//   Плюс: невидимая "крышка" сверху, непрерывные столкновения, ограничение скорости,
//   мелкий шаг физики и респаун, если шарик всё-таки оказался снаружи.

using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using Vuforia;
// псевдонимы: в Vuforia тоже есть класс Image, чтобы не было конфликта имён
using Image = UnityEngine.UI.Image;
using Text = UnityEngine.UI.Text;
using Outline = UnityEngine.UI.Outline;
using Graphic = UnityEngine.UI.Graphic;
using CanvasScaler = UnityEngine.UI.CanvasScaler;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

[DisallowMultipleComponent]
public class MazeGame : MonoBehaviour
{
    public enum GravitySource
    {
        Auto,          // телефон: реальный наклон картинки (датчики); ПК: лабиринт "лежит ровно", наклон с клавиатуры/мыши
        PhoneSensors,  // гравитация из акселерометра/гироскопа телефона
        WorldDown,     // гравитация = мировой низ Unity (как в видео, нужен Vuforia Device Tracking)
        TargetDown     // гравитация всегда "в пол картинки", катаем только клавишами/пальцем
    }

    enum State { Playing, Won }

    [Header("Объекты лабиринта")]
    [Tooltip("Видимый шарик (можно оставить пустым, если объект называется Ball/Шар)")]
    public Transform ball;
    [Tooltip("Финиш/лунка: пустой объект или куб в конце лабиринта (можно назвать Finish/End)")]
    public Transform finish;
    [Tooltip("AR-камера (если пусто — Camera.main)")]
    public Camera arCamera;

    [Header("Физика")]
    public GravitySource gravitySource = GravitySource.Auto;
    [Tooltip("Сила тяжести. Лабиринт внутри симуляции всегда ~20 единиц в ширину.")]
    public float gravity = 55f;
    public float maxBallSpeed = 30f;
    [Range(0f, 1f)] public float friction = 0.2f;
    [Range(0f, 1f)] public float bounciness = 0.05f;
    [Tooltip("Наклон меньше этого угла (от датчиков) игнорируется — шарик не дрожит на ровной картинке")]
    public float sensorDeadZone = 2f;
    [Tooltip("Усиление реального наклона картинки: 2 = наклонил на 10°, шарик катится как от 20°")]
    public float realTiltBoost = 2f;
    [Tooltip("Останавливать шарик, когда картинка потеряна камерой")]
    public bool pauseWhenTargetLost = true;
    [Tooltip("Шагов физики в секунду (больше = надёжнее столкновения)")]
    public int physicsRate = 100;

    [Header("Управление (ПК + телефон)")]
    [Tooltip("Максимальный виртуальный наклон лабиринта от клавиш/мыши/пальца, градусы")]
    public float maxTiltAngle = 28f;
    public float tiltSmoothing = 12f;
    [Tooltip("Сколько нужно провести пальцем/мышью для полного наклона (доля от меньшей стороны экрана)")]
    public float dragRadius = 0.12f;
    [Tooltip("Катать шарик наклоном самого телефона (относительно положения, в котором ты его держал, когда нашлась картинка)")]
    public bool phoneTiltControl = true;
    [Tooltip("На сколько градусов наклонить телефон для полного наклона лабиринта")]
    public float phoneTiltForFull = 15f;
    public bool showControlsHint = true;
    [Tooltip("Писать в лог (adb logcat) состояние датчиков раз в 2 секунды")]
    public bool debugLog = true;

    [Header("Финиш")]
    [Tooltip("Радиус лунки в единицах лабиринта (0 = авто по размеру Finish)")]
    public float finishRadius = 0f;
    [Tooltip("Нарисовать чёрную лунку, если у Finish нет своей модели")]
    public bool createHoleVisual = true;
    [Tooltip("Сколько секунд летит конфетти")]
    public float effectTime = 4f;
    [Tooltip("Авто-перезапуск через N секунд после победы (0 = ждать нажатия)")]
    public float autoRestartDelay = 0f;
    public string winText = "WIN";

    [Header("Свои эффекты (необязательно)")]
    [Tooltip("Своя панель победы (Canvas). Если пусто — создаётся как в видео")]
    public GameObject winPanel;
    [Tooltip("Свой эффект победы (Particle System). Если пусто — создаётся конфетти")]
    public GameObject winEffect;
    public Material confettiMaterial;
    [Tooltip("Звук победы. Если пусто — генерируется короткая мелодия")]
    public AudioClip winSound;
    [Tooltip("Звук качения шарика (зациклен, громкость от скорости)")]
    public AudioClip rollSound;

    // --- внутреннее ---
    Transform maze;
    ObserverBehaviour observer;
    Transform simRoot;
    Rigidbody simBall;
    readonly List<GameObject> spawned = new List<GameObject>();

    Bounds mazeBounds;          // в локальных координатах лабиринта
    Bounds safeBounds;
    float simScale;
    float ballRadius;           // локальные единицы лабиринта
    float floorTop;
    Vector3 startLocal;
    Vector3 finishLocal;
    float finishRadiusLocal;
    Vector3 ballBaseScale;

    Vector3 baseLocalPos;
    Quaternion baseLocalRot;
    Vector3 pivotLocal;
    Vector2 tilt;

    Vector3 sensorCam;
    bool hasSensor;
    Vector2 phoneRef;
    bool phoneRefSet;
    Vector2 phoneTiltInput;
    float nextLogTime;

    State state = State.Playing;
    bool paused;
    Vector3 savedVel, savedAngVel;
    Coroutine winRoutine;
    float winShownTime = -1f;

    bool pointerWasDown;
    Vector2 pointerStart;

    ParticleSystem confetti;
    AudioSource sfx, rollSrc;
    RectTransform winBox;
    Graphic controlsHint;

    // ======================================================================
    void Start()
    {
        if (arCamera == null) arCamera = Camera.main != null ? Camera.main : FindAnyObjectByType<Camera>();

        // Если скрипт повесили прямо на ImageTarget — Vuforia перезаписывает его поворот,
        // поэтому создаём промежуточный объект-пивот и переносим туда всё содержимое.
        if (GetComponent<ObserverBehaviour>() != null)
        {
            var pivot = new GameObject("MazePivot").transform;
            pivot.SetParent(transform, false);
            var kids = new List<Transform>();
            foreach (Transform c in transform) if (c != pivot) kids.Add(c);
            foreach (var k in kids) k.SetParent(pivot, true);
            maze = pivot;
        }
        else maze = transform;

        observer = GetComponentInParent<ObserverBehaviour>();

        if (ball == null) ball = FindChildByName(maze, "ball", "шар", "sphere");
        if (finish == null) finish = FindChildByName(maze, "finish", "end", "exit", "goal", "hole", "финиш", "выход");
        if (ball == null)
        {
            Debug.LogError("[MazeGame] Не найден шарик. Перетащи его в поле Ball.", this);
            enabled = false;
            return;
        }
        if (finish == null) Debug.LogWarning("[MazeGame] Не задан Finish — победы не будет. Создай пустой объект в конце лабиринта и укажи его.", this);

        if (physicsRate > 0) Time.fixedDeltaTime = 1f / physicsRate;
        Screen.sleepTimeout = SleepTimeout.NeverSleep;

#if ENABLE_LEGACY_INPUT_MANAGER
        if (SystemInfo.supportsGyroscope) Input.gyro.enabled = true;
#endif

        baseLocalPos = maze.localPosition;
        baseLocalRot = maze.localRotation;
        ballBaseScale = ball.localScale;

        PrepareVisualBall();
        BuildSimulation();
        SetupFinish();
        BuildAudio();
        BuildWinUI();
        BuildConfetti();

        ResetBall();
    }

    void OnDestroy()
    {
        foreach (var go in spawned) if (go != null) Destroy(go);
    }

    // ======================================================================
    // Подготовка
    // ======================================================================
    void PrepareVisualBall()
    {
        // Старый шарик больше не участвует в физике — он только показывает позицию из симуляции.
        var rb = ball.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.detectCollisions = false;
        }
        foreach (var mb in ball.GetComponents<MonoBehaviour>())
        {
            if (mb == this || !mb.enabled) continue;
            Debug.LogWarning($"[MazeGame] Отключаю скрипт {mb.GetType().Name} на шарике — шариком теперь управляет MazeGame.", mb);
            mb.enabled = false;
        }
    }

    void BuildSimulation()
    {
        // 1) Собираем коллайдеры стен/пола (без шарика, финиша и триггеров)
        var sources = new List<Collider>();
        foreach (var c in maze.GetComponentsInChildren<Collider>(true))
        {
            if (c.isTrigger) continue;
            if (c.transform.IsChildOf(ball)) continue;
            if (finish != null && c.transform.IsChildOf(finish)) continue;
            if (!(c is BoxCollider || c is SphereCollider || c is CapsuleCollider || c is MeshCollider)) continue;
            sources.Add(c);
        }

        bool fromRenderers = sources.Count == 0;
        var rendererMeshes = new List<MeshFilter>();
        if (fromRenderers)
        {
            Debug.LogWarning("[MazeGame] У стен нет коллайдеров — строю их по моделям.", this);
            foreach (var mf in maze.GetComponentsInChildren<MeshFilter>(true))
            {
                if (mf.sharedMesh == null || mf.transform.IsChildOf(ball)) continue;
                if (finish != null && mf.transform.IsChildOf(finish)) continue;
                rendererMeshes.Add(mf);
            }
        }

        // 2) Границы лабиринта в его локальных координатах
        bool any = false;
        mazeBounds = new Bounds();
        void Grow(Bounds b) { if (!any) { mazeBounds = b; any = true; } else mazeBounds.Encapsulate(b); }
        foreach (var c in sources) Grow(TransformBounds(LocalShapeBounds(c), RelMatrix(c.transform)));
        foreach (var mf in rendererMeshes) Grow(TransformBounds(mf.sharedMesh.bounds, RelMatrix(mf.transform)));

        // 3) Радиус и старт шарика
        float ballWorldR;
        var sc = ball.GetComponent<SphereCollider>();
        if (sc != null) ballWorldR = sc.radius * MaxAbs(ball.lossyScale);
        else
        {
            var bmf = ball.GetComponent<MeshFilter>();
            ballWorldR = bmf != null && bmf.sharedMesh != null
                ? MaxAbs(Vector3.Scale(bmf.sharedMesh.bounds.extents, ball.lossyScale))
                : 0.5f * MaxAbs(ball.lossyScale);
        }
        ballRadius = ballWorldR / Mathf.Max(MaxAbs(maze.lossyScale), 1e-6f);
        startLocal = maze.InverseTransformPoint(ball.position);

        if (!any) mazeBounds = new Bounds(startLocal, Vector3.one * ballRadius * 20f);

        // 4) Масштаб симуляции: лабиринт ~20 единиц (на маленьких размерах PhysX ведёт себя плохо)
        simScale = 20f / Mathf.Max(mazeBounds.size.x, mazeBounds.size.z, 1e-5f);

        var rootGo = new GameObject("[MazeSim]");
        spawned.Add(rootGo);
        simRoot = rootGo.transform;
        simRoot.position = new Vector3(0f, -1000f, 0f);
        simRoot.rotation = Quaternion.identity;
        simRoot.localScale = Vector3.one * simScale;

        var mat = new PhysicsMaterial("MazeMat")
        {
            dynamicFriction = friction,
            staticFriction = friction,
            bounciness = bounciness,
            frictionCombine = PhysicsMaterialCombine.Average,
            bounceCombine = PhysicsMaterialCombine.Minimum
        };

        foreach (var c in sources) CopyCollider(c, MakeSimChild(c.transform, "sim_" + c.name), mat);
        foreach (var mf in rendererMeshes)
        {
            var go = MakeSimChild(mf.transform, "sim_" + mf.name);
            if (mf.sharedMesh.isReadable)
            {
                var mc = go.AddComponent<MeshCollider>();
                mc.sharedMesh = mf.sharedMesh;
                mc.sharedMaterial = mat;
            }
            else
            {
                var bc = go.AddComponent<BoxCollider>();
                bc.center = mf.sharedMesh.bounds.center;
                bc.size = mf.sharedMesh.bounds.size;
                bc.sharedMaterial = mat;
            }
        }

        // 5) Пол: ищем лучом под шариком. Если пола нет — добавляем.
        Physics.SyncTransforms();
        floorTop = mazeBounds.min.y;
        bool floorFound = false;
        float bestDist = float.MaxValue;
        Vector3 origin = simRoot.TransformPoint(startLocal);
        foreach (var hit in Physics.RaycastAll(origin, Vector3.down, 1000f, ~0, QueryTriggerInteraction.Ignore))
        {
            if (!hit.collider.transform.IsChildOf(simRoot)) continue;
            if (hit.distance < bestDist)
            {
                bestDist = hit.distance;
                floorTop = simRoot.InverseTransformPoint(hit.point).y;
                floorFound = true;
            }
        }
        float size = Mathf.Max(mazeBounds.size.x, mazeBounds.size.z);
        float thick = size * 0.1f;
        if (!floorFound)
        {
            AddSimBox("sim_SafetyFloor",
                new Vector3(mazeBounds.center.x, floorTop - thick * 0.5f, mazeBounds.center.z),
                new Vector3(mazeBounds.size.x + size * 0.2f, thick, mazeBounds.size.z + size * 0.2f), mat);
        }
        if (startLocal.y < floorTop + ballRadius) startLocal.y = floorTop + ballRadius * 1.01f;

        // 6) Невидимая крышка поверх стен — шарик физически не может вылететь вверх
        float lidBottom = Mathf.Max(mazeBounds.max.y, floorTop + ballRadius * 2.1f, startLocal.y + ballRadius * 1.02f);
        AddSimBox("sim_Lid",
            new Vector3(mazeBounds.center.x, lidBottom + thick * 0.5f, mazeBounds.center.z),
            new Vector3(mazeBounds.size.x + size * 0.2f, thick, mazeBounds.size.z + size * 0.2f), mat);

        // Зона, за пределами которой шарик респаунится
        safeBounds = new Bounds(mazeBounds.center, mazeBounds.size);
        safeBounds.SetMinMax(
            new Vector3(mazeBounds.min.x - ballRadius * 4f, floorTop - ballRadius * 6f, mazeBounds.min.z - ballRadius * 4f),
            new Vector3(mazeBounds.max.x + ballRadius * 4f, lidBottom + ballRadius * 6f, mazeBounds.max.z + ballRadius * 4f));

        pivotLocal = new Vector3(mazeBounds.center.x, floorTop, mazeBounds.center.z);

        // 7) Физический шарик
        var ballGo = new GameObject("[MazeSimBall]");
        spawned.Add(ballGo);
        ballGo.transform.position = simRoot.TransformPoint(startLocal);
        var col = ballGo.AddComponent<SphereCollider>();
        col.radius = ballRadius * simScale;
        col.sharedMaterial = mat;
        simBall = ballGo.AddComponent<Rigidbody>();
        simBall.useGravity = false;
        simBall.mass = 1f;
        simBall.linearDamping = 0.05f;
        simBall.angularDamping = 0.02f;   // большое торможение вращения работало как ручник
        simBall.interpolation = RigidbodyInterpolation.Interpolate;
        simBall.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        simBall.maxDepenetrationVelocity = 3f;   // если вдруг перекрылся со стеной — не выстреливает
        simBall.maxAngularVelocity = 60f;
        simBall.solverIterations = 12;
        simBall.solverVelocityIterations = 4;
    }

    void SetupFinish()
    {
        if (finish == null) return;

        Bounds fb = new Bounds(maze.InverseTransformPoint(finish.position), Vector3.zero);
        bool hasShape = false;
        var fc = finish.GetComponentInChildren<Collider>(true);
        if (fc != null) { fb = TransformBounds(LocalShapeBounds(fc), RelMatrix(fc.transform)); hasShape = true; }
        else
        {
            var fmf = finish.GetComponentInChildren<MeshFilter>(true);
            if (fmf != null && fmf.sharedMesh != null) { fb = TransformBounds(fmf.sharedMesh.bounds, RelMatrix(fmf.transform)); hasShape = true; }
        }
        finishLocal = fb.center;

        if (finishRadius > 0f) finishRadiusLocal = finishRadius;
        else if (hasShape) finishRadiusLocal = Mathf.Max(Mathf.Min(fb.extents.x, fb.extents.z), ballRadius * 0.9f);
        else finishRadiusLocal = ballRadius * 1.5f;

        // Чёрная лунка, как в видео
        if (createHoleVisual && finish.GetComponentInChildren<Renderer>(true) == null)
        {
            var hole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            hole.name = "Hole";
            Destroy(hole.GetComponent<Collider>());
            hole.transform.SetParent(maze, false);
            float d = Mathf.Max(finishRadiusLocal, ballRadius * 1.3f) * 2f;
            hole.transform.localPosition = new Vector3(finishLocal.x, floorTop + ballRadius * 0.02f, finishLocal.z);
            hole.transform.localRotation = Quaternion.identity;
            hole.transform.localScale = new Vector3(d, ballRadius * 0.01f, d);
            var r = hole.GetComponent<Renderer>();
            var src = FirstMazeMaterial();
            var m = src != null ? new Material(src) : new Material(r.sharedMaterial);
            m.color = new Color(0.02f, 0.02f, 0.02f, 1f);
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.1f);
            r.sharedMaterial = m;
        }
    }

    void BuildAudio()
    {
        sfx = gameObject.AddComponent<AudioSource>();
        sfx.playOnAwake = false;
        sfx.spatialBlend = 0f;
        if (winSound == null) winSound = MakeWinJingle();

        if (rollSound != null)
        {
            rollSrc = ball.gameObject.AddComponent<AudioSource>();
            rollSrc.clip = rollSound;
            rollSrc.loop = true;
            rollSrc.playOnAwake = false;
            rollSrc.spatialBlend = 0f;
            rollSrc.volume = 0f;
        }
    }

    // ======================================================================
    // Игровой цикл
    // ======================================================================
    void Update()
    {
        if (simBall == null) return;

        // Потеря картинки -> пауза
        bool wasPaused = paused;
        paused = pauseWhenTargetLost && observer != null && !IsTracked(observer);
        UpdateBallKinematic();

        ReadSensors();
        if (wasPaused && !paused) phoneRefSet = false; // картинка нашлась — текущее положение телефона = "ровно"
        UpdatePhoneTilt();

        // Ввод: клавиатура + наклон телефона + мышь/палец
        Vector2 target = ReadKeyboardTilt() + phoneTiltInput;
        bool down = ReadPointer(out Vector2 pos);
        bool pressedThisFrame = down && !pointerWasDown;
        if (pressedThisFrame) pointerStart = pos;
        if (down && state == State.Playing)
        {
            float r = Mathf.Max(1f, Mathf.Min(Screen.width, Screen.height) * dragRadius);
            target += (pos - pointerStart) / r;
        }
        pointerWasDown = down;
        target = Vector2.ClampMagnitude(target, 1f);
        if (state != State.Playing || paused) target = Vector2.zero;
        tilt = Vector2.Lerp(tilt, target, 1f - Mathf.Exp(-tiltSmoothing * Time.deltaTime));
        ApplyTilt();

        // Перезапуск
        if (RestartKeyDown()) ResetBall();
        else if (state == State.Won && winShownTime >= 0f && Time.time - winShownTime > 0.8f
                 && (pressedThisFrame || ContinueKeyDown()))
            ResetBall();

        // Лёгкая пульсация надписи WIN
        if (state == State.Won && winBox != null && winShownTime >= 0f && Time.time - winShownTime > 0.4f)
            winBox.localScale = Vector3.one * (1f + 0.03f * Mathf.Sin((Time.time - winShownTime) * 5f));

        if (debugLog && Time.unscaledTime >= nextLogTime)
        {
            nextLogTime = Time.unscaledTime + 2f;
            Debug.Log($"[MazeGame] src={ResolveSource()} sensor={hasSensor} sensorCam={sensorCam:F2} phoneTilt={phoneTiltInput:F2} tilt={tilt:F2} " +
                      $"paused={paused} state={state} gLocal={GravityLocalDir():F2} speed={simBall.linearVelocity.magnitude:F2} orient={Screen.orientation}");
        }

        // Звук качения
        if (rollSrc != null)
        {
            float speed = (state == State.Playing && !paused) ? simBall.linearVelocity.magnitude : 0f;
            float k = Mathf.Clamp01(speed / (maxBallSpeed * 0.5f));
            rollSrc.volume = Mathf.Lerp(rollSrc.volume, k, 1f - Mathf.Exp(-10f * Time.deltaTime));
            rollSrc.pitch = 0.8f + 0.5f * k;
            if (speed > 0.3f && !rollSrc.isPlaying) rollSrc.Play();
            else if (speed <= 0.3f && rollSrc.isPlaying && rollSrc.volume < 0.02f) rollSrc.Pause();
        }
    }

    void FixedUpdate()
    {
        if (simBall == null || state != State.Playing || paused) return;

        simBall.AddForce(GravityLocalDir() * gravity, ForceMode.Acceleration);

        Vector3 v = simBall.linearVelocity;
        if (v.sqrMagnitude > maxBallSpeed * maxBallSpeed) simBall.linearVelocity = v.normalized * maxBallSpeed;

        Vector3 lp = simRoot.InverseTransformPoint(simBall.position);
        if (!safeBounds.Contains(lp)) { ResetBall(); return; }

        if (finish != null)
        {
            Vector2 d = new Vector2(lp.x - finishLocal.x, lp.z - finishLocal.z);
            if (d.magnitude < finishRadiusLocal) winRoutine = StartCoroutine(WinRoutine());
        }
    }

    void LateUpdate()
    {
        if (simBall == null || state != State.Playing) return;
        ball.position = maze.TransformPoint(simRoot.InverseTransformPoint(simBall.transform.position));
        ball.rotation = maze.rotation * simBall.transform.rotation;
    }

    public void ResetBall()
    {
        if (simBall == null) return;
        if (winRoutine != null) { StopCoroutine(winRoutine); winRoutine = null; }

        ball.gameObject.SetActive(true);
        ball.localScale = ballBaseScale;

        state = State.Playing;
        savedVel = savedAngVel = Vector3.zero;
        Vector3 p = simRoot.TransformPoint(startLocal);
        simBall.transform.position = p;
        simBall.position = p;
        UpdateBallKinematic();
        if (!simBall.isKinematic)
        {
            simBall.linearVelocity = Vector3.zero;
            simBall.angularVelocity = Vector3.zero;
        }
        tilt = Vector2.zero;
        phoneRefSet = false;
        winShownTime = -1f;

        if (winPanel != null) winPanel.SetActive(false);
        if (winEffect != null) winEffect.SetActive(false);
        if (confetti != null) confetti.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    void UpdateBallKinematic()
    {
        bool shouldBeKinematic = state != State.Playing || paused;
        if (shouldBeKinematic && !simBall.isKinematic)
        {
            savedVel = simBall.linearVelocity;
            savedAngVel = simBall.angularVelocity;
            simBall.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            simBall.isKinematic = true;
        }
        else if (!shouldBeKinematic && simBall.isKinematic)
        {
            simBall.isKinematic = false;
            simBall.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            simBall.linearVelocity = savedVel;
            simBall.angularVelocity = savedAngVel;
        }
    }

    // ======================================================================
    // Победа
    // ======================================================================
    IEnumerator WinRoutine()
    {
        state = State.Won;
        UpdateBallKinematic();
        if (rollSrc != null) rollSrc.Stop();

        // Шарик закатывается в лунку и проваливается
        Vector3 from = simRoot.InverseTransformPoint(simBall.position);
        Vector3 above = new Vector3(finishLocal.x, from.y, finishLocal.z);
        Vector3 below = above + Vector3.down * ballRadius * 2.2f;
        for (float t = 0f; t < 0.2f; t += Time.deltaTime)
        {
            ball.position = maze.TransformPoint(Vector3.Lerp(from, above, t / 0.2f));
            yield return null;
        }
        for (float t = 0f; t < 0.35f; t += Time.deltaTime)
        {
            float k = t / 0.35f;
            ball.position = maze.TransformPoint(Vector3.Lerp(above, below, k * k));
            ball.localScale = ballBaseScale * Mathf.Lerp(1f, 0.5f, k);
            yield return null;
        }
        ball.gameObject.SetActive(false);

        // WIN + конфетти + звук
        if (winSound != null) sfx.PlayOneShot(winSound);
        if (winEffect != null)
        {
            winEffect.SetActive(true);
            foreach (var ps in winEffect.GetComponentsInChildren<ParticleSystem>(true)) ps.Play(true);
        }
        else if (confetti != null) confetti.Play(true);
        if (winPanel != null) winPanel.SetActive(true);
        winShownTime = Time.time;
        if (winBox != null) StartCoroutine(PopIn(winBox));

        if (effectTime > 0f)
        {
            yield return new WaitForSeconds(effectTime);
            if (confetti != null) confetti.Stop(true, ParticleSystemStopBehavior.StopEmitting);
            if (winEffect != null)
                foreach (var ps in winEffect.GetComponentsInChildren<ParticleSystem>(true)) ps.Stop(true);
        }
        if (autoRestartDelay > 0f)
        {
            yield return new WaitForSeconds(autoRestartDelay);
            winRoutine = null;
            ResetBall();
        }
    }

    IEnumerator PopIn(RectTransform rt)
    {
        for (float t = 0f; t < 0.35f; t += Time.unscaledDeltaTime)
        {
            float k = t / 0.35f;
            float s = k < 0.7f ? Mathf.Lerp(0.2f, 1.15f, k / 0.7f) : Mathf.Lerp(1.15f, 1f, (k - 0.7f) / 0.3f);
            rt.localScale = Vector3.one * s;
            yield return null;
        }
        rt.localScale = Vector3.one;
    }

    // ======================================================================
    // Гравитация и наклон
    // ======================================================================
    GravitySource ResolveSource()
    {
        switch (gravitySource)
        {
            case GravitySource.Auto:
                return Application.isMobilePlatform && hasSensor ? GravitySource.PhoneSensors : GravitySource.TargetDown;
            case GravitySource.PhoneSensors:
                return hasSensor ? GravitySource.PhoneSensors : GravitySource.TargetDown;
            default:
                return gravitySource;
        }
    }

    Quaternion BasisRotation => maze.parent != null ? maze.parent.rotation : Quaternion.identity;

    // Направление "вниз" в локальных координатах лабиринта
    Vector3 GravityLocalDir()
    {
        var src = ResolveSource();
        Vector3 gBasis;
        if (src == GravitySource.PhoneSensors && arCamera != null)
            gBasis = Quaternion.Inverse(BasisRotation) * (arCamera.transform.rotation * sensorCam);
        else if (src == GravitySource.WorldDown)
            gBasis = Quaternion.Inverse(BasisRotation) * Vector3.down;
        else
            gBasis = Vector3.down;

        gBasis.Normalize();
        if (src != GravitySource.TargetDown) gBasis = ApplyDeadZone(gBasis);
        // наклон maze.localRotation (клавиши/палец) учитывается здесь автоматически
        return Quaternion.Inverse(maze.localRotation) * gBasis;
    }

    Vector3 ApplyDeadZone(Vector3 g)
    {
        if (g.y >= 0f) return g;
        Vector3 h = new Vector3(g.x, 0f, g.z);
        float s = h.magnitude;
        float dz = Mathf.Sin(sensorDeadZone * Mathf.Deg2Rad);
        float ns = Mathf.Clamp01(Mathf.Max(0f, s - dz) / (1f - dz) * Mathf.Max(0f, realTiltBoost));
        if (ns <= 0f) return Vector3.down;
        h = h / s * ns;
        return new Vector3(h.x, -Mathf.Sqrt(1f - ns * ns), h.z);
    }

    void ApplyTilt()
    {
        Quaternion q = Quaternion.identity;
        if (tilt.sqrMagnitude > 1e-6f && arCamera != null)
        {
            // направления "вправо" и "от себя" относительно камеры, спроецированные на плоскость картинки
            Quaternion inv = Quaternion.Inverse(BasisRotation);
            Vector3 r = inv * arCamera.transform.right; r.y = 0f;
            Vector3 f = inv * arCamera.transform.forward; f.y = 0f;
            if (f.sqrMagnitude < 0.04f) { f = inv * arCamera.transform.up; f.y = 0f; }
            if (r.sqrMagnitude > 1e-6f && f.sqrMagnitude > 1e-6f)
            {
                Vector3 d = r.normalized * tilt.x + f.normalized * tilt.y;
                float mag = Mathf.Clamp01(d.magnitude);
                if (mag > 1e-4f)
                {
                    Vector3 n = (Vector3.up + d.normalized * Mathf.Tan(maxTiltAngle * mag * Mathf.Deg2Rad)).normalized;
                    q = Quaternion.FromToRotation(Vector3.up, n);
                }
            }
        }

        Quaternion rot = q * baseLocalRot;
        Vector3 scaledPivot = Vector3.Scale(maze.localScale, pivotLocal);
        Vector3 pivotInParent = baseLocalPos + baseLocalRot * scaledPivot;
        maze.localRotation = rot;
        maze.localPosition = pivotInParent - rot * scaledPivot;
    }

    // Наклон телефона относительно "нулевого" положения -> виртуальный наклон лабиринта (x — вправо по экрану, y — вверх/от себя)
    void UpdatePhoneTilt()
    {
        phoneTiltInput = Vector2.zero;
        if (!phoneTiltControl || !hasSensor || state != State.Playing || paused) return;
        Vector2 s = new Vector2(sensorCam.x, sensorCam.y);
        if (!phoneRefSet) { phoneRef = s; phoneRefSet = true; return; }
        Vector2 d = s - phoneRef;
        float dz = Mathf.Sin(1.5f * Mathf.Deg2Rad);
        float mag = d.magnitude;
        if (mag <= dz) return;
        d = d / mag * (mag - dz);
        phoneTiltInput = Vector2.ClampMagnitude(d / Mathf.Sin(Mathf.Max(1f, phoneTiltForFull) * Mathf.Deg2Rad), 1f);
    }

    void ReadSensors()
    {
        if (!TryReadDeviceGravity(out Vector3 raw) || raw.sqrMagnitude < 0.01f) return;
        Vector3 cam = DeviceToCamera(raw).normalized;
        sensorCam = hasSensor ? Vector3.Slerp(sensorCam, cam, 1f - Mathf.Exp(-12f * Time.deltaTime)) : cam;
        hasSensor = true;
    }

    // Датчики телефона дают вектор в координатах устройства; переводим в координаты камеры с учётом ориентации экрана
    static Vector3 DeviceToCamera(Vector3 a)
    {
        Vector2 s;
        switch (Screen.orientation)
        {
            case ScreenOrientation.LandscapeLeft: s = new Vector2(-a.y, a.x); break;
            case ScreenOrientation.LandscapeRight: s = new Vector2(a.y, -a.x); break;
            case ScreenOrientation.PortraitUpsideDown: s = new Vector2(-a.x, -a.y); break;
            default: s = new Vector2(a.x, a.y); break;
        }
        return new Vector3(s.x, s.y, -a.z);
    }

    static bool IsTracked(ObserverBehaviour ob)
    {
        var st = ob.TargetStatus.Status;
        return st == Status.TRACKED || st == Status.EXTENDED_TRACKED;
    }

    // ======================================================================
    // Ввод (поддерживает и старый Input Manager, и новый Input System)
    // ======================================================================
    static Vector2 ReadKeyboardTilt()
    {
        float x = 0f, y = 0f;
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null)
        {
            if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
            if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
            if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
            if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
        }
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.GetKey(KeyCode.D) || Input.GetKey(KeyCode.RightArrow)) x += 1f;
        if (Input.GetKey(KeyCode.A) || Input.GetKey(KeyCode.LeftArrow)) x -= 1f;
        if (Input.GetKey(KeyCode.W) || Input.GetKey(KeyCode.UpArrow)) y += 1f;
        if (Input.GetKey(KeyCode.S) || Input.GetKey(KeyCode.DownArrow)) y -= 1f;
#endif
        return Vector2.ClampMagnitude(new Vector2(x, y), 1f);
    }

    static bool ReadPointer(out Vector2 pos)
    {
#if ENABLE_INPUT_SYSTEM
        var ts = Touchscreen.current;
        if (ts != null && ts.primaryTouch.press.isPressed) { pos = ts.primaryTouch.position.ReadValue(); return true; }
        var m = Mouse.current;
        if (m != null && m.leftButton.isPressed) { pos = m.position.ReadValue(); return true; }
#elif ENABLE_LEGACY_INPUT_MANAGER
        if (Input.touchCount > 0) { pos = Input.GetTouch(0).position; return true; }
        if (Input.GetMouseButton(0)) { pos = Input.mousePosition; return true; }
#endif
        pos = default;
        return false;
    }

    static bool RestartKeyDown()
    {
#if ENABLE_INPUT_SYSTEM
        return Keyboard.current != null && Keyboard.current.rKey.wasPressedThisFrame;
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.R);
#else
        return false;
#endif
    }

    static bool ContinueKeyDown()
    {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        return kb != null && (kb.spaceKey.wasPressedThisFrame || kb.enterKey.wasPressedThisFrame);
#elif ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.Return);
#else
        return false;
#endif
    }

    static bool TryReadDeviceGravity(out Vector3 g)
    {
#if ENABLE_LEGACY_INPUT_MANAGER
        if (SystemInfo.supportsGyroscope && Input.gyro.enabled)
        {
            g = Input.gyro.gravity;
            if (g.sqrMagnitude > 0.01f) return true;
        }
        g = Input.acceleration;
        return g.sqrMagnitude > 0.01f;
#elif ENABLE_INPUT_SYSTEM
        var gs = GravitySensor.current;
        if (gs != null)
        {
            if (!gs.enabled) InputSystem.EnableDevice(gs);
            g = gs.gravity.ReadValue();
            if (g.sqrMagnitude > 0.01f) return true;
        }
        var acc = Accelerometer.current;
        if (acc != null)
        {
            if (!acc.enabled) InputSystem.EnableDevice(acc);
            g = acc.acceleration.ReadValue();
            return g.sqrMagnitude > 0.01f;
        }
        g = default;
        return false;
#else
        g = default;
        return false;
#endif
    }

    // ======================================================================
    // UI победы (как в видео: серая плашка с зелёной рамкой и градиентной надписью WIN)
    // ======================================================================
    void BuildWinUI()
    {
        bool needCanvas = winPanel == null || showControlsHint;
        if (!needCanvas) return;

        var canvasGo = new GameObject("[MazeUI]", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler));
        spawned.Add(canvasGo);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 500;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight = 0.5f;

        if (winPanel == null)
        {
            var panel = new GameObject("WinPanel", typeof(RectTransform));
            var prt = (RectTransform)panel.transform;
            prt.SetParent(canvasGo.transform, false);
            prt.anchorMin = Vector2.zero; prt.anchorMax = Vector2.one;
            prt.offsetMin = prt.offsetMax = Vector2.zero;

            var box = new GameObject("Box", typeof(RectTransform), typeof(Image), typeof(Outline));
            winBox = (RectTransform)box.transform;
            winBox.SetParent(prt, false);
            winBox.sizeDelta = new Vector2(720f, 330f);
            winBox.anchoredPosition = new Vector2(0f, 60f);
            box.GetComponent<Image>().color = new Color(0.62f, 0.62f, 0.62f, 0.6f);
            var ol = box.GetComponent<Outline>();
            ol.effectColor = new Color(0.25f, 1f, 0.25f, 1f);
            ol.effectDistance = new Vector2(7f, -7f);

            CreateText(winBox, winText, 220f, true, Vector2.zero, new Vector2(720f, 330f));
            string hint = Application.isMobilePlatform ? "Нажми на экран, чтобы сыграть ещё" : "Клик / Пробел / R — сыграть ещё";
            CreateText(prt, hint, 46f, false, new Vector2(0f, -170f), new Vector2(1600f, 90f));

            panel.SetActive(false);
            winPanel = panel;
        }

        if (showControlsHint)
        {
            string text = Application.isMobilePlatform
                ? "Наклоняй телефон или веди пальцем по экрану"
                : "WASD / стрелки или тяни мышью — наклон лабиринта · R — заново";
            var rt = new GameObject("ControlsHintRoot", typeof(RectTransform)).GetComponent<RectTransform>();
            rt.SetParent(canvasGo.transform, false);
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0f);
            rt.anchoredPosition = new Vector2(0f, 70f);
            rt.sizeDelta = new Vector2(1800f, 80f);
            controlsHint = CreateText(rt, text, 38f, false, Vector2.zero, new Vector2(1800f, 80f));
            StartCoroutine(FadeHint());
        }
    }

    IEnumerator FadeHint()
    {
        yield return new WaitForSeconds(7f);
        if (controlsHint != null) controlsHint.CrossFadeAlpha(0f, 1.5f, false);
    }

    static Graphic CreateText(Transform parent, string s, float size, bool winStyle, Vector2 pos, Vector2 rectSize)
    {
        var go = new GameObject("Text", typeof(RectTransform));
        var rt = (RectTransform)go.transform;
        rt.SetParent(parent, false);
        rt.anchoredPosition = pos;
        rt.sizeDelta = rectSize;

        var pink = new Color32(255, 70, 220, 255);
        var purple = new Color32(120, 60, 255, 255);

        TMP_FontAsset font = null;
        try { font = TMP_Settings.defaultFontAsset; } catch { /* TMP Essentials не импортированы */ }

        if (font != null)
        {
            var t = go.AddComponent<TextMeshProUGUI>();
            t.font = font;
            t.text = s;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.raycastTarget = false;
            if (winStyle)
            {
                t.fontStyle = FontStyles.Bold;
                t.color = Color.white;
                t.enableVertexGradient = true;
                t.colorGradient = new VertexGradient(pink, purple, pink, purple);
                t.outlineWidth = 0.12f;
                t.outlineColor = new Color32(40, 0, 70, 255);
            }
            else
            {
                t.color = Color.white;
                t.outlineWidth = 0.2f;
                t.outlineColor = new Color32(0, 0, 0, 255);
            }
            return t;
        }
        else
        {
            var t = go.AddComponent<Text>();
            t.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            t.text = s;
            t.fontSize = Mathf.RoundToInt(size);
            t.fontStyle = winStyle ? FontStyle.Bold : FontStyle.Normal;
            t.alignment = TextAnchor.MiddleCenter;
            t.horizontalOverflow = HorizontalWrapMode.Overflow;
            t.verticalOverflow = VerticalWrapMode.Overflow;
            t.raycastTarget = false;
            t.color = winStyle ? (Color)pink : Color.white;
            var ol = go.AddComponent<Outline>();
            ol.effectColor = winStyle ? (Color)purple : Color.black;
            ol.effectDistance = new Vector2(4f, -4f);
            return t;
        }
    }

    // ======================================================================
    // Конфетти
    // ======================================================================
    void BuildConfetti()
    {
        if (winEffect != null) { winEffect.SetActive(false); return; }

        float size = Mathf.Max(mazeBounds.size.x, mazeBounds.size.z);
        var go = new GameObject("WinConfetti");
        go.SetActive(false);
        go.transform.SetParent(maze, false);
        go.transform.localPosition = new Vector3(mazeBounds.center.x, floorTop, mazeBounds.center.z);
        confetti = go.AddComponent<ParticleSystem>();

        var main = confetti.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = Mathf.Max(0.5f, effectTime);
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        main.maxParticles = 800;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.6f, 2.6f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(size * 0.8f, size * 1.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(size * 0.018f, size * 0.035f);
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f);

        var colors = new Gradient { mode = GradientMode.Fixed };
        colors.SetKeys(
            new[]
            {
                new GradientColorKey(new Color(1f, 0.2f, 0.3f), 0.17f),
                new GradientColorKey(new Color(1f, 0.85f, 0.1f), 0.34f),
                new GradientColorKey(new Color(0.2f, 1f, 0.3f), 0.5f),
                new GradientColorKey(new Color(0.2f, 0.8f, 1f), 0.67f),
                new GradientColorKey(new Color(0.5f, 0.3f, 1f), 0.84f),
                new GradientColorKey(new Color(1f, 0.3f, 0.9f), 1f)
            },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 1f) });
        main.startColor = new ParticleSystem.MinMaxGradient(colors) { mode = ParticleSystemGradientMode.RandomColor };

        var em = confetti.emission;
        em.rateOverTime = 60f;
        em.SetBursts(new[] { new ParticleSystem.Burst(0f, 180) });

        var shape = confetti.shape;
        shape.shapeType = ParticleSystemShapeType.Box;
        shape.rotation = new Vector3(-90f, 0f, 0f); // излучение вверх
        shape.scale = new Vector3(mazeBounds.size.x, mazeBounds.size.z, 0.01f);
        shape.randomDirectionAmount = 0.35f;

        var force = confetti.forceOverLifetime;
        force.enabled = true;
        force.space = ParticleSystemSimulationSpace.Local;
        force.x = new ParticleSystem.MinMaxCurve(0f);
        force.y = new ParticleSystem.MinMaxCurve(-size * 2.2f);
        force.z = new ParticleSystem.MinMaxCurve(0f);

        var rot = confetti.rotationOverLifetime;
        rot.enabled = true;
        rot.z = new ParticleSystem.MinMaxCurve(-8f, 8f);

        var col = confetti.colorOverLifetime;
        col.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.75f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(fade);

        var pr = go.GetComponent<ParticleSystemRenderer>();
        pr.renderMode = ParticleSystemRenderMode.Billboard;
        Material m = confettiMaterial;
        if (m == null)
        {
            foreach (var name in new[] { "Sprites/Default", "UI/Default", "Universal Render Pipeline/Particles/Unlit", "Particles/Standard Unlit" })
            {
                var sh = Shader.Find(name);
                if (sh != null) { m = new Material(sh); break; }
            }
        }
        if (m != null) pr.sharedMaterial = m;

        go.SetActive(true);
        confetti.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    // ======================================================================
    // Утилиты
    // ======================================================================
    Matrix4x4 RelMatrix(Transform t) => maze.worldToLocalMatrix * t.localToWorldMatrix;

    GameObject MakeSimChild(Transform src, string name)
    {
        Matrix4x4 rel = RelMatrix(src);
        var go = new GameObject(name);
        go.transform.SetParent(simRoot, false);
        go.transform.localPosition = rel.GetPosition();
        go.transform.localRotation = rel.rotation;
        go.transform.localScale = rel.lossyScale;
        return go;
    }

    void AddSimBox(string name, Vector3 localCenter, Vector3 localSize, PhysicsMaterial mat)
    {
        var go = new GameObject(name);
        go.transform.SetParent(simRoot, false);
        go.transform.localPosition = localCenter;
        var bc = go.AddComponent<BoxCollider>();
        bc.size = localSize;
        bc.sharedMaterial = mat;
    }

    static void CopyCollider(Collider src, GameObject dst, PhysicsMaterial mat)
    {
        Collider c = null;
        switch (src)
        {
            case BoxCollider b:
                var nb = dst.AddComponent<BoxCollider>(); nb.center = b.center; nb.size = b.size; c = nb; break;
            case SphereCollider s:
                var ns = dst.AddComponent<SphereCollider>(); ns.center = s.center; ns.radius = s.radius; c = ns; break;
            case CapsuleCollider cc:
                var nc = dst.AddComponent<CapsuleCollider>();
                nc.center = cc.center; nc.radius = cc.radius; nc.height = cc.height; nc.direction = cc.direction; c = nc; break;
            case MeshCollider m:
                var nm = dst.AddComponent<MeshCollider>(); nm.sharedMesh = m.sharedMesh; nm.convex = m.convex; c = nm; break;
        }
        if (c != null) c.sharedMaterial = mat;
    }

    static Bounds LocalShapeBounds(Collider c)
    {
        switch (c)
        {
            case BoxCollider b: return new Bounds(b.center, b.size);
            case SphereCollider s: return new Bounds(s.center, Vector3.one * s.radius * 2f);
            case CapsuleCollider cc:
                Vector3 sz = Vector3.one * cc.radius * 2f;
                sz[cc.direction] = Mathf.Max(cc.height, cc.radius * 2f);
                return new Bounds(cc.center, sz);
            case MeshCollider m: return m.sharedMesh != null ? m.sharedMesh.bounds : new Bounds();
        }
        return new Bounds();
    }

    static Bounds TransformBounds(Bounds b, Matrix4x4 m)
    {
        Vector3 c = b.center, e = b.extents;
        var r = new Bounds(m.MultiplyPoint3x4(c + new Vector3(-e.x, -e.y, -e.z)), Vector3.zero);
        for (int i = 1; i < 8; i++)
        {
            var corner = c + new Vector3((i & 1) != 0 ? e.x : -e.x, (i & 2) != 0 ? e.y : -e.y, (i & 4) != 0 ? e.z : -e.z);
            r.Encapsulate(m.MultiplyPoint3x4(corner));
        }
        return r;
    }

    static float MaxAbs(Vector3 v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.y), Mathf.Abs(v.z));

    static Transform FindChildByName(Transform root, params string[] keys)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t == root) continue;
            string n = t.name.ToLowerInvariant();
            foreach (var k in keys) if (n.Contains(k)) return t;
        }
        return null;
    }

    Material FirstMazeMaterial()
    {
        foreach (var r in maze.GetComponentsInChildren<Renderer>(true))
        {
            if (r.transform.IsChildOf(ball)) continue;
            if (r.sharedMaterial != null) return r.sharedMaterial;
        }
        return null;
    }

    static AudioClip MakeWinJingle()
    {
        const int sr = 44100;
        float[] notes = { 523.25f, 659.25f, 783.99f, 1046.5f };
        const float step = 0.11f, tail = 0.6f;
        int n = Mathf.CeilToInt((step * (notes.Length - 1) + tail) * sr);
        var data = new float[n];
        for (int k = 0; k < notes.Length; k++)
        {
            int start = Mathf.RoundToInt(k * step * sr);
            float len = k == notes.Length - 1 ? tail : step * 1.6f;
            int count = Mathf.Min(n - start, Mathf.RoundToInt(len * sr));
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)sr;
                float env = Mathf.Exp(-t * (k == notes.Length - 1 ? 4f : 9f)) * Mathf.Clamp01(t * 200f);
                float w = Mathf.Sin(2f * Mathf.PI * notes[k] * t) + 0.3f * Mathf.Sin(4f * Mathf.PI * notes[k] * t);
                data[start + i] += w * env * 0.3f;
            }
        }
        var clip = AudioClip.Create("WinJingle", n, 1, sr, false);
        clip.SetData(data, 0);
        return clip;
    }
}
