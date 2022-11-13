using Assets;
using Assets.Scripts;
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using static Unity.Mathematics.math;

public class Submarine : MonoBehaviour
{
    private static Submarine _instance;
    public static Submarine Instance => _instance ??= FindObjectOfType<Submarine>();


    [Header("General")]
    [SerializeField]
    private float ambientEnergyUsage = 10;

    [SerializeField]
    private CircleTarget circleTargetPrefab;

    [SerializeField]
    private Canvas UICanvas;

    [SerializeField]
    private Camera mainCamera;

    [field: SerializeField]
    public ChunkObjectsDictionary ChunkDictionary { get; private set; }

    [SerializeField]
    private Graphic blackFader;

    [SerializeField]
    private AmbientMusic ambientMusic;


    [Header("Audio")]
    [SerializeField]
    private AudioMixerGroup mainMixerGroup;

    [SerializeField]
    private Vector2 minMaxPitch = new Vector2(0.1f, 1f);

    [SerializeField]
    private Vector2 minMaxVolume = new Vector2(0.1f, 1f);

    [SerializeField]
    private Vector2 minMaxSpeed = new Vector2(1f, 1f);

    [SerializeField]
    private Vector2 minMaxAudioAmount = new Vector2(0f, 1.5f);

    [SerializeField]
    private AudioClip miningSound;

    [SerializeField]
    private Vector2 miningSoundPitchRange = new Vector2(0.75f, 1.25f);

    [SerializeField]
    private float miningSoundVolume = 1f;

    [SerializeField]
    private float miningSoundFrequency = 10f;

    [Header("Digging")]
    [SerializeField]
    private bool enableBrush;

    [SerializeField]
    private float brushSpeed = 5f;

    [SerializeField]
    private float diggingEnergyUsage = 15;

    [SerializeField]
    private Transform laser1;

    [SerializeField]
    private Transform laser2;

    [Header("Physics")]
    [SerializeField]
    private float rotationSpeed = 7f;

    [SerializeField]
    private float rotationInterpolationSpeed = 7f;

    [SerializeField]
    private float acceleration = 10f;

    [SerializeField]
    private float velocityLimit = 20f;

    [SerializeField]
    private float hitThreshold = 1f;

    [SerializeField]
    private float damageMultiplier = 1f;

    [SerializeField]
    private Map map;

    [field: SerializeField]
    public bool UseMouseControls { get; private set; } = true;

    [Header("Events")]
    public UnityEvent<float> OnHealthStart;
    public UnityEvent<float> OnEnergyStart;
    public UnityEvent<int> OnObjectsStart;
    public UnityEvent OnGameStart;

    public UnityEvent<float> OnHit;
    public UnityEvent OnDeath;
    public UnityEvent OnWin;
    public UnityEvent<float> OnEnergyUsageChange;
    public UnityEvent<float> OnEnergyChange;
    public UnityEvent<FindableObject> OnObjectFound;


    [Header("Sounds")]
    [SerializeField]
    private AudioClip metalHitSound;

    [Header("Particles")]
    [SerializeField]
    private PooledParticles DigParticles;

    [SerializeField]
    private PooledParticles CrashParticles;
    private Rigidbody rb;
    private AudioSource audioSource;
    private Vector3 previousRotation;
    private Vector3 destinationRotation;
    private Quaternion currentRotation;
    private float _energyUsage = 0;
    public float EnergyUsage
    {
        get => _energyUsage;
        set
        {
            if (_energyUsage != value)
            {
                _energyUsage = value;
                OnEnergyUsageChange.Invoke(_energyUsage);
            }
        }
    }

    public float SubHeight { get; private set; }

    [field: SerializeField]
    public float Health { get; private set; }

    [field: SerializeField]
    public float MaxHealth { get; private set; } = 100f;

    [field: SerializeField]
    public float Energy { get; private set; }

    [field: SerializeField]
    public float MaxEnergy { get; private set; } = 100f;

    [field: SerializeField]
    public int ObjectsToFind { get; private set; } = 5;

    public int ObjectsFound { get; private set; }

    private float audioFadeInTime = 3;
    private float audioFadeInTimer = float.PositiveInfinity;

    public static bool GameOver = false;
    public static bool GameWin = true;

    public bool StartSubSounds = false;

    public static event Action OnGameReload;

    private SpriteRenderer laserSprite1;
    private SpriteRenderer laserSprite2;

    static Submarine()
    {
        OnGameReload += Submarine_OnGameReload;
    }

    private static void Submarine_OnGameReload()
    {
        GameOver = false;
        GameWin = true;
        _instance = null;
    }

    private void Awake()
    {
        laserSprite1 = laser1.GetComponentInChildren<SpriteRenderer>();
        laserSprite2 = laser2.GetComponentInChildren<SpriteRenderer>();
        _instance = this;
        ChunkObjectsDictionary.InitDictionary();
    }

    private IEnumerator Test()
    {
        yield return new WaitForSeconds(5f);
        //Application.LoadLevel(Application.loadedLevel);
        OnGameReload?.Invoke();
        SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
    }

    public void Retry()
    {
        OnGameReload?.Invoke();
        blackFader.gameObject.SetActive(true);
        blackFader.color = new Color(blackFader.color.r, blackFader.color.g, blackFader.color.b, 0f);
        StartCoroutine(CrossFadeAlpha(blackFader, 1f, 0.5f));
        StartCoroutine(FadeOutSubSounds(0.5f));
        StartCoroutine(ambientMusic.FadeOut(0.75f));
        StartCoroutine(RetryRoutine());
    }

    private IEnumerator CrossFadeAlpha(Graphic graphic, float alpha, float time)
    {
        float oldAlpha = graphic.color.a;

        for (float t = 0; t < time; t += Time.deltaTime)
        {
            graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, lerp(oldAlpha, alpha, t / time));
            yield return null;
        }

        graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, alpha);
    }

    private IEnumerator RetryRoutine()
    {
        yield return new WaitForSeconds(1f);
        SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
    }

    public void Quit()
    {
        Application.Quit();
    }

    private void Lose()
    {
        GameOver = true;
        OnDeath.Invoke();
    }

    private void Win()
    {
        GameWin = true;
        OnWin.Invoke();
    }

    private void OnEnable()
    {
        audioFadeInTimer = 0;
    }

    // Start is called before the first frame update
    private void Start()
    {
        EnergyUsage += ambientEnergyUsage;

        Health = MaxHealth;
        Energy = MaxEnergy;

        OnHealthStart.Invoke(MaxHealth);
        OnEnergyStart.Invoke(MaxEnergy);
        OnObjectsStart.Invoke(ObjectsToFind);

        rb = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();
        //targetRotation = transform.rotation;
        //oldMousePosition = Input.mousePosition;
        //targetRotation = transform.rotation;
        //StartCoroutine(TestDraw());
        destinationRotation = transform.eulerAngles;

        UpdateAudioEffects();

        //camera.aspect = 16f / 9f;
    }

    private float InputAdjustmentMultiplier(float frameRate)
    {
        return 1f;
        //return sqrt(1f / GetDegreesPerHZ(frameRate));
        //return 1f / ();

    }

    private float GetDegreesPerHZ(float hz)
    {
        //return -0.01736287f * hz + 3.320607f;
        return 32504.92f * pow(hz, -2.148081f); //* x ^{ -2.148081}
    }

    /*IEnumerator TestDraw()
    {
        yield return new WaitForSeconds(2f);
        map.UseSphereBrush(transform.position, true, 1000f);
    }*/

    //bool inFocus = false;

    public bool UsingBrush { get; private set; } = false;

    private IEnumerator FadeOutSubSounds(float time)
    {
        for (float t = 0; t < time; t += Time.deltaTime)
        {
            audioFadeInTimer = lerp(audioFadeInTime, 0f, t / time);
            yield return null;
        }
        audioFadeInTimer = 0f;
    }

    // Update is called once per frame
    private void Update()
    {
        if (StartSubSounds && audioFadeInTimer < audioFadeInTime)
        {
            audioFadeInTimer += Time.deltaTime;
            if (audioFadeInTimer > audioFadeInTime)
            {
                audioFadeInTimer = audioFadeInTime;
            }
        }
        SubHeight = transform.position.y;
        if (!GameOver && !GameWin)
        {
            //if (inFocus)
            //{
            //previousRotation = destinationRotation;
            destinationRotation += new Vector3(-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2), Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2));
            //rb.angularVelocity += new Vector3(-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime),2), Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2));



            //rb.angularVelocity += (Quaternion.AngleAxis(-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2), transform.rotation * Vector3.forward) * Quaternion.AngleAxis(Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2), transform.rotation * Vector3.right)).eulerAngles;
            //currentRotation = transform.rotation;


            //var rotation = transform.eulerAngles;

            //transform.rotation = Quaternion.Slerp(transform.rotation,Quaternion.Euler(rotation.x, rotation.y,0f),rotationInterpolationSpeed * Time.deltaTime);
            if (destinationRotation.x > 89f)
            {
                destinationRotation.x = 89f;
            }
            else if (destinationRotation.x < -89f)
            {
                destinationRotation.x = -89f;
            }

            //currentRotation = Quaternion.Euler(destinationRotation);
            currentRotation = Quaternion.Slerp(currentRotation, Quaternion.Euler(destinationRotation), rotationInterpolationSpeed * Time.deltaTime);

            //currentRotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(destinationRotation), rotationInterpolationSpeed * Time.deltaTime);

            //transform.eulerAngles = destinationRotation;
            transform.rotation = currentRotation;

            //transform.rotation = Quaternion.AngleAxis(-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime, transform.right) * Quaternion.AngleAxis(Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime,transform.up) * transform.rotation;


            /*var rotationIntensity = lerp(new float2(0f),new float2(90f),new float2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y")));


            Debug.Log("INput Adjustment = " + (InputAdjustmentMultiplier(1f / Time.deltaTime)));

            var xRotation = currentRotation.x + (-rotationIntensity.y * rotationSpeed * InputAdjustmentMultiplier(1f / Time.deltaTime) * Time.deltaTime);
            var yRotation = currentRotation.y + (rotationIntensity.x * rotationSpeed * InputAdjustmentMultiplier(1f / Time.deltaTime) * Time.deltaTime);

            if (xRotation > 89f)
            {
                xRotation = 89f;
            }
            else if (xRotation < -89f)
            {
                xRotation = -89f;
            }
            currentRotation = new Vector3(xRotation,yRotation,0f);
            transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.Euler(currentRotation), rotationInterpolationSpeed * Time.deltaTime);
            */


            Vector3 velocityChange = transform.TransformVector(Vector3.forward * acceleration * Input.GetAxis("Vertical") * Time.deltaTime);
            velocityChange += transform.TransformVector(Vector3.right * acceleration * Input.GetAxis("Horizontal") * Time.deltaTime);

            Vector3 totalVelocity = rb.velocity + velocityChange;

            if (!(rb.velocity.magnitude > velocityLimit && totalVelocity.magnitude > rb.velocity.magnitude))
            {
                rb.velocity = totalVelocity;
            }
            //}
            UpdateTarget();
        }


        UpdateAudioEffects();
    }

    private void UpdateAudioEffects()
    {
        float soundAmount = clamp(unlerp(0f, velocityLimit, rb.velocity.magnitude), minMaxAudioAmount.x, minMaxAudioAmount.y);

        audioSource.pitch = lerp(minMaxPitch.x, minMaxPitch.y, soundAmount);
        audioSource.volume = lerp(0f, lerp(minMaxVolume.x, minMaxVolume.y, soundAmount), audioFadeInTimer / audioFadeInTime);
    }

    private void FixedUpdate()
    {
        if (!GameOver && !GameWin && enableBrush)
        {
            /*if (Input.GetKey(KeyCode.Mouse0))
            {
                UseBrush(new Vector2(Screen.width / 2f, Screen.height / 2f), false);
            }*/
            if (Input.GetKey(KeyCode.Mouse0))
            {

                //UseBrush(new Vector2(Screen.width / 2f, Screen.height / 2f), true);
                UseBrush(new Ray(transform.position, currentRotation * Vector3.forward), true);
                if (!UsingBrush)
                {
                    UsingBrush = true;
                    laser1.gameObject.SetActive(true);
                    laser2.gameObject.SetActive(true);
                    EnergyUsage += diggingEnergyUsage;
                }
            }
            else
            {
                if (UsingBrush)
                {
                    UsingBrush = false;
                    laser1.gameObject.SetActive(false);
                    laser2.gameObject.SetActive(false);
                    EnergyUsage -= diggingEnergyUsage;
                }
            }
        }

        if (!GameOver && !GameWin)
        {
            Energy -= EnergyUsage / 20f * Time.fixedDeltaTime;
            //Debug.Log("ENERGY = " + Energy);
            OnEnergyChange.Invoke(Energy);

            if (Energy <= 0)
            {
                Lose();
            }
        }

        if (GameOver || GameWin)
        {
            laser1.gameObject.SetActive(false);
            laser2.gameObject.SetActive(false);
        }
    }

    private void UseBrush(Ray ray, bool eraseMode)
    {
        //Debug.DrawLine(transform.position, ray.origin + (ray.direction * 5f), Color.red, 5f);;
        float sample = map.SamplePoint(transform.position);
        if (sample < map.IsoLevel)
        {
            //Ray ray = map.MainCamera.ScreenPointToRay(screenPosition);

            if (map.FireRayParallel(ray, out float3 hit, 10f, map.BoundsSize / map.NumPointsPerAxis))
            {
                float distance = Vector3.Distance(transform.position, hit);
                if (distance >= 0f && distance <= 10f)
                {
                    map.UseSphereBrush(hit, eraseMode, Time.fixedDeltaTime * (brushSpeed / 10f), new int3(1.5));
                    DigParticles.Spawn(hit, Quaternion.identity);
                    //Debug.DrawLine(transform.position, ray.origin + (ray.direction * distance), Color.red,5f);
                    //Debug.DrawLine(transform.position, hit, Color.green, 5f);
                    if (PlayMiningSound(hit))
                    {
                        UpdateLaser(hit, laser1, laserSprite1);
                        UpdateLaser(hit, laser2, laserSprite2);
                    }
                    //map.UseCubeBrush(hit,eraseMode,10f,new int3(5,6,7));
                }
            }
        }
    }

    private void UpdateLaser(Vector3 target, Transform laser, SpriteRenderer sprite)
    {
        Vector3 randomDirection = UnityEngine.Random.insideUnitSphere;

        Vector3 point = target + (randomDirection * 0.5f);

        laser.LookAt(point);

        float distance = length(point - laserSprite1.transform.position);

        sprite.transform.localScale = new Vector3(1, 0.737f * distance, 1f);

        sprite.transform.localPosition = new Vector3(0f, 0f, 0.5f * distance);
    }

    private List<ContactPoint> contacts = new List<ContactPoint>(1);

    private void OnCollisionEnter(Collision collision)
    {
        int numPoints = collision.GetContacts(contacts);
        if (numPoints > 0)
        {
            //var directionToHit = (contacts[0].point - transform.position).normalized;

            //Debug.Log();
            //var intensity = dot(directionToHit, collision.impulse);
            float intensity = length(collision.impulse);
            //Debug.Log("Intensity = " + intensity);

            //Debug.Log("HIT = " + intensity);

            if (intensity > hitThreshold)
            {
                TakeHit(contacts[0].point, intensity * damageMultiplier);
            }
        }
    }

    private void TakeHit(Vector3 collisionPoint, float intensity)
    {
        Health -= intensity;
        OnHit.Invoke(intensity);
        audioSource.PlayOneShot(metalHitSound, (intensity / damageMultiplier / (hitThreshold * 1.5f)));
        CrashParticles.Spawn(collisionPoint, Quaternion.identity);
        CrashParticles.Spawn(collisionPoint, Quaternion.identity);

        if (Health <= 0f)
        {
            Lose();
        }
    }

    private float miningSoundTimer = 0f;

    private bool PlayMiningSound(Vector3 source)
    {
        miningSoundTimer -= Time.fixedDeltaTime;
        if (miningSoundTimer <= 0)
        {
            miningSoundTimer += 1f / miningSoundFrequency;

            AudioSource instance = AudioPool.PlayAtPoint(source, miningSound, mainMixerGroup);
            instance.volume = miningSoundVolume;
            instance.pitch = UnityEngine.Random.Range(miningSoundPitchRange.x, miningSoundPitchRange.y);
            return true;
        }
        return false;
    }

    private CircleTarget circleTargetInstance;
    private bool targetShown;
    private FindableObject previousNearestObject = null;

    private void UpdateTarget()
    {
        float nearestDistance = float.PositiveInfinity;
        FindableObject nearestObject = null;

        foreach (FindableObject obj in FindableObject.NearbyObjects)
        {
            float centeredness = dot(transform.rotation * Vector3.forward, (obj.transform.TransformPoint(obj.TargetOffset) - transform.position).normalized) + 1.01f;

            //Debug.Log("CenteredNess = " + centeredness);
            //var len = length(obj.transform.position - transform.position) * (1f / centeredness);
            float len = (1f / centeredness) + (length(obj.transform.TransformPoint(obj.TargetOffset) - transform.position) / 10000f);// + sqrt(length(obj.transform.position - transform.position));
            if (centeredness >= 1.95f && IsTargetVisible(obj) && len < nearestDistance)
            {
                nearestDistance = len;
                nearestObject = obj;
            }
        }

        if ((nearestObject == null || nearestObject != previousNearestObject) && circleTargetInstance != null)
        {
            circleTargetInstance.Selected = false;
            circleTargetInstance.DestroyTarget();
            circleTargetInstance = null;
            targetShown = false;
        }

        if (nearestObject != null && circleTargetInstance == null)
        {
            circleTargetInstance = GameObject.Instantiate(circleTargetPrefab, UICanvas.transform);
            circleTargetInstance.TargetCanvas = UICanvas;
            circleTargetInstance.TargetCamera = mainCamera;
            circleTargetInstance.TargetObject = nearestObject;
            targetShown = true;
        }


        /*if (nearestObject == null && circleTargetInstance != null)
        {
            //GameObject.Destroy(circleTargetInstance);
            circleTargetInstance.DestroyTarget();
            circleTargetInstance = null;
            targetShown = false;
        }
        else if (nearestObject != null && circleTargetInstance == null)
        {
            circleTargetInstance = GameObject.Instantiate(circleTargetPrefab,UICanvas.transform);
            targetShown = true;
        }*/

        previousNearestObject = nearestObject;

        /*if (nearestObject != null)
        {
            var canvasSize = UICanvas.GetComponent<RectTransform>().sizeDelta;

            var rt = circleTargetInstance.GetComponent<RectTransform>();


            rt.anchoredPosition = mainCamera.WorldToViewportPoint(nearestObject.transform.position + nearestObject.TargetOffset) * canvasSize;
            rt.sizeDelta = new Vector2(1500f,1500f) * (1f / length(nearestObject.transform.position - transform.position)) * nearestObject.TargetSize * nearestObject.transform.localScale;
            //circleTargetInstance.transform.localPosition = mainCamera.WorldToScreenPoint(nearestObject.transform.position);
            //Debug.Log("LOCATION = " + circleTargetInstance.GetComponent<RectTransform>().anchoredPosition);
        }*/
    }

    public void FoundAnObject(FindableObject obj)
    {
        OnObjectFound.Invoke(obj);

        Vector3 position = obj.transform.position;

        List<FindableObject> destroyedObjects = new List<FindableObject>();

        foreach (FindableObject nearby in FindableObject.NearbyObjects)
        {
            if (lengthsq(position - nearby.transform.position) <= 0.2f)
            {
                destroyedObjects.Add(nearby);
            }
        }

        foreach (FindableObject dObject in destroyedObjects)
        {
            MainPool.Return(dObject.gameObject);
        }

        //Destroy(obj.gameObject);
        ObjectsFound++;

        if (ObjectsFound >= ObjectsToFind)
        {
            Win();
        }
    }

    public void StartGame()
    {
        GameWin = false;
        Cursor.lockState = CursorLockMode.Locked;
        OnGameStart.Invoke();
        blackFader.CrossFadeAlpha(0f, 0.5f, false);
        StartCoroutine(Wait(0.75f, () => blackFader.gameObject.SetActive(false)));
        ambientMusic.gameObject.SetActive(true);
        StartSubSounds = true;
    }

    private IEnumerator Wait(float time, Action onDone)
    {
        yield return new WaitForSeconds(time);
        onDone?.Invoke();
    }

    /*public void ShowTarget(FindableObject target)
    {

    }

    public void HideTarget()
    {

    }*/

    private static Plane[] planeCache = new Plane[6];

    private static bool IsVisibleFrom(Bounds bounds, Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, planeCache);
        return GeometryUtility.TestPlanesAABB(planeCache, bounds);
    }

    private bool IsTargetVisible(FindableObject target)
    {
        Ray ray = new Ray(transform.position, (target.transform.position - transform.position).normalized);
        return map.FireRayParallel(ray, out _, length(target.transform.position - transform.position) - 0.5f, map.BoundsSize / map.NumPointsPerAxis) == false && IsVisibleFrom(new Bounds(target.transform.position, target.transform.localScale), mainCamera);
    }
}
