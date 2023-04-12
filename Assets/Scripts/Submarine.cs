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
    // Singleton instance of the Submarine class.
    private static Submarine _instance;
    public static Submarine Instance => _instance ??= FindObjectOfType<Submarine>();

    [Header("General")]

    // The amount of energy consumed per second by the submarine when no actions are performed.
    [SerializeField]
    private float ambientEnergyUsage = 10;

    // The prefab used to instantiate circle targets.
    [SerializeField]
    private CircleTarget circleTargetPrefab;

    // The canvas used to display UI elements.
    [SerializeField]
    private Canvas UICanvas;

    // The camera used to render the scene.
    [SerializeField]
    private Camera mainCamera;

    // A dictionary that maps chunk coordinates to chunk game objects.
    [field: SerializeField]
    public ChunkObjectsDictionary ChunkDictionary { get; private set; }

    // A graphic component used to fade the screen to black.
    [SerializeField]
    private Graphic blackFader;

    // The ambient music player.
    [SerializeField]
    private AmbientMusic ambientMusic;

    [Header("Audio")]

    // The audio mixer group used to control the submarine's audio.
    [SerializeField]
    private AudioMixerGroup mainMixerGroup;

    // The minimum and maximum pitch values for the submarine's audio.
    [SerializeField]
    private Vector2 minMaxPitch = new Vector2(0.1f, 1f);

    // The minimum and maximum volume values for the submarine's audio.
    [SerializeField]
    private Vector2 minMaxVolume = new Vector2(0.1f, 1f);

    // The minimum and maximum audio amount values for the submarine's audio.
    [SerializeField]
    private Vector2 minMaxAudioAmount = new Vector2(0f, 1.5f);

    // The audio clip played when the submarine mines a resource.
    [SerializeField]
    private AudioClip miningSound;

    // The range of pitch values for the mining sound.
    [SerializeField]
    private Vector2 miningSoundPitchRange = new Vector2(0.75f, 1.25f);

    // The volume of the mining sound.
    [SerializeField]
    private float miningSoundVolume = 1f;

    // The frequency at which the mining sound is played.
    [SerializeField]
    private float miningSoundFrequency = 10f;

    [Header("Digging")]

    // Whether the brush tool is enabled.
    [SerializeField]
    private bool enableBrush;

    // The speed at which the brush tool digs.
    [SerializeField]
    private float brushSpeed = 5f;

    // The amount of energy consumed per second by the submarine when digging.
    [SerializeField]
    private float diggingEnergyUsage = 15;

    // The first laser transform used for digging.
    [SerializeField]
    private Transform laser1;

    // The second laser transform used for digging.
    [SerializeField]
    private Transform laser2;

    [Header("Physics")]

    // The speed at which the submarine rotates.
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

    // Events for health, energy, objects, and game state
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

    // Serialized fields for audio, particles, and other game objects
    [Header("Sounds")]
    [SerializeField]
    private AudioClip metalHitSound;

    [Header("Particles")]
    [SerializeField]
    private PooledParticles DigParticles;

    [SerializeField]
    private PooledParticles CrashParticles;

    // Other private variables
    private Rigidbody rb;
    private AudioSource audioSource;
    private Vector3 previousRotation;
    private Vector3 destinationRotation;
    private Quaternion currentRotation;
    private float _energyUsage = 0;

    // Getter and setter for energy usage
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

    public float SubHeight { get; private set; }  // The height of the submarine

    [field: SerializeField]
    public float Health { get; private set; }  // The current health of the submarine

    [field: SerializeField]
    public float MaxHealth { get; private set; } = 100f;  // The maximum health of the submarine

    [field: SerializeField]
    public float Energy { get; private set; }  // The current energy of the submarine

    [field: SerializeField]
    public float MaxEnergy { get; private set; } = 100f;  // The maximum energy of the submarine

    [field: SerializeField]
    public int ObjectsToFind { get; private set; } = 5;  // The number of objects the player needs to find

    public int ObjectsFound { get; private set; }  // The number of objects the player has found so far

    private float audioFadeInTime = 3;  // The amount of time it takes for audio to fade in
    private float audioFadeInTimer = float.PositiveInfinity;  // A timer used for fading in audio

    public static bool GameOver = false;  // A flag indicating whether the game is over
    public static bool GameWin = true;  // A flag indicating whether the game has been won

    public bool StartSubSounds = false;  // A flag indicating whether to start playing submarine sounds

    public static event Action OnGameReload;  // An event that is triggered when the game is reloaded

    private SpriteRenderer laserSprite1;  // The sprite renderer for the first laser
    private SpriteRenderer laserSprite2;  // The sprite renderer for the second laser

    static Submarine()
    {
        OnGameReload += Submarine_OnGameReload;  // Register a method to be called when the OnGameReload event is triggered
    }

    private static void Submarine_OnGameReload()
    {
        GameOver = false;  // Reset the GameOver flag
        GameWin = true;  // Reset the GameWin flag
        _instance = null;  // Reset the static _instance variable to null
    }

    private void Awake()
    {
        laserSprite1 = laser1.GetComponentInChildren<SpriteRenderer>();  // Get the sprite renderer for the first laser
        laserSprite2 = laser2.GetComponentInChildren<SpriteRenderer>();  // Get the sprite renderer for the second laser
        _instance = this;  // Set the static _instance variable to this instance of the Submarine class
        ChunkObjectsDictionary.InitDictionary();  // Initialize the ChunkObjectsDictionary
    }

    //Used for retrying the game
    public void Retry()
    {
        // Invoke the OnGameReload event, if subscribed to
        OnGameReload?.Invoke();

        // Activate the black fader object and set its initial alpha to 0
        blackFader.gameObject.SetActive(true);
        blackFader.color = new Color(blackFader.color.r, blackFader.color.g, blackFader.color.b, 0f);

        // Fade the alpha of the black fader object to 1 over 0.5 seconds
        StartCoroutine(CrossFadeAlpha(blackFader, 1f, 0.5f));

        // Fade out the submarine sounds over 0.5 seconds
        StartCoroutine(FadeOutSubSounds(0.5f));

        // Fade out the ambient music over 0.75 seconds
        StartCoroutine(ambientMusic.FadeOut(0.75f));

        // Start the RetryRoutine coroutine
        StartCoroutine(RetryRoutine());
    }

    // Coroutine to cross fade the alpha of a Graphic object over a given time
    private IEnumerator CrossFadeAlpha(Graphic graphic, float alpha, float time)
    {
        // Get the initial alpha of the graphic
        float oldAlpha = graphic.color.a;

        // Loop over the given time and lerp between the old alpha and the new alpha
        for (float t = 0; t < time; t += Time.deltaTime)
        {
            graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, lerp(oldAlpha, alpha, t / time));
            yield return null;
        }

        // Set the final alpha of the graphic
        graphic.color = new Color(graphic.color.r, graphic.color.g, graphic.color.b, alpha);
    }

    // Coroutine to wait for 1 second and then reload the active scene
    private IEnumerator RetryRoutine()
    {
        yield return new WaitForSeconds(1f);
        SceneManager.LoadSceneAsync(SceneManager.GetActiveScene().name, LoadSceneMode.Single);
    }

    // Method to quit the application
    public void Quit()
    {
        Application.Quit();
    }

    // Method to call when the submarine loses the game
    private void Lose()
    {
        GameOver = true;
        OnDeath.Invoke();
    }

    // Method to call when the submarine wins the game
    private void Win()
    {
        GameWin = true;
        OnWin.Invoke();
    }

    // Called when the component is enabled
    private void OnEnable()
    {
        audioFadeInTimer = 0;
    }

    // Called when the component is started
    private void Start()
    {
        // Add the ambient energy usage to the total energy usage
        EnergyUsage += ambientEnergyUsage;

        // Set the initial health, energy, and objects to find
        Health = MaxHealth;
        Energy = MaxEnergy;

        // Invoke the OnHealthStart, OnEnergyStart, and OnObjectsStart events
        OnHealthStart.Invoke(MaxHealth);
        OnEnergyStart.Invoke(MaxEnergy);
        OnObjectsStart.Invoke(ObjectsToFind);

        // Get the Rigidbody and AudioSource components and set the destination rotation to the current rotation
        rb = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();
        destinationRotation = transform.eulerAngles;

        // Update the audio effects
        UpdateAudioEffects();
    }

    public bool UsingBrush { get; private set; } = false;  // A boolean property that indicates whether the brush is being used or not

    // This coroutine fades out the submarine's audio sounds over a certain period of time
    private IEnumerator FadeOutSubSounds(float time)
    {
        // Loop until the specified time has elapsed
        for (float t = 0; t < time; t += Time.deltaTime)
        {
            // Use linear interpolation to gradually reduce the audio fade-in timer to 0 over time
            audioFadeInTimer = lerp(audioFadeInTime, 0f, t / time);
            yield return null;
        }
        // Set the audio fade-in timer to 0 when the coroutine is finished
        audioFadeInTimer = 0f;
    }

    // This method is called every frame
    private void Update()
    {
        // Quit the game if the Escape key is pressed
        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Quit();
        }
        // If submarine sounds are starting and the audio fade-in timer is less than the fade-in time, gradually increase the audio fade-in timer
        if (StartSubSounds && audioFadeInTimer < audioFadeInTime)
        {
            audioFadeInTimer += Time.deltaTime;
            if (audioFadeInTimer > audioFadeInTime)
            {
                audioFadeInTimer = audioFadeInTime;
            }
        }
        // Set the submarine height to the current y position of the submarine
        SubHeight = transform.position.y;
        // If the game is not over and the player has not won, allow the player to control the submarine
        if (!GameOver && !GameWin)
        {
            // Rotate the submarine based on mouse input
            destinationRotation += new Vector3(-Input.GetAxis("Mouse Y") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2), Input.GetAxis("Mouse X") * rotationSpeed * Time.deltaTime * pow(rsqrt(Time.deltaTime), 2));

            // Clamp the rotation to prevent the submarine from rotating too far up or down
            if (destinationRotation.x > 89f)
            {
                destinationRotation.x = 89f;
            }
            else if (destinationRotation.x < -89f)
            {
                destinationRotation.x = -89f;
            }

            // Use spherical linear interpolation to smoothly rotate the submarine towards its destination rotation
            currentRotation = Quaternion.Slerp(currentRotation, Quaternion.Euler(destinationRotation), rotationInterpolationSpeed * Time.deltaTime);

            // Apply the current rotation to the submarine's transform
            transform.rotation = currentRotation;

            // Calculate the desired change in velocity based on player input
            Vector3 velocityChange = transform.TransformVector(Vector3.forward * acceleration * Input.GetAxis("Vertical") * Time.deltaTime);
            velocityChange += transform.TransformVector(Vector3.right * acceleration * Input.GetAxis("Horizontal") * Time.deltaTime);

            // Add the velocity change to the submarine's current velocity
            Vector3 totalVelocity = rb.velocity + velocityChange;

            // Limit the submarine's velocity to the velocity limit, unless the new velocity is greater than the current velocity
            if (!(rb.velocity.magnitude > velocityLimit && totalVelocity.magnitude > rb.velocity.magnitude))
            {
                rb.velocity = totalVelocity;
            }

            // Update the target circle position
            UpdateTarget();
        }

        // Update the submarine's audio effects
        UpdateAudioEffects();
    }

    // Update the audio effects based on the submarine's velocity
    private void UpdateAudioEffects()
    {
        // Determine the sound amount based on the submarine's velocity
        float soundAmount = clamp(unlerp(0f, velocityLimit, rb.velocity.magnitude), minMaxAudioAmount.x, minMaxAudioAmount.y);

        // Set the pitch of the audio source based on the sound amount
        audioSource.pitch = lerp(minMaxPitch.x, minMaxPitch.y, soundAmount);

        // Set the volume of the audio source based on the sound amount and the fade-in timer
        audioSource.volume = lerp(0f, lerp(minMaxVolume.x, minMaxVolume.y, soundAmount), audioFadeInTimer / audioFadeInTime);
    }

    // Update the physics of the submarine in the FixedUpdate method
    private void FixedUpdate()
    {
        // Check if the game is not over and the brush is enabled
        if (!GameOver && !GameWin && enableBrush)
        {
            // Check if the left mouse button is pressed and the brush can be used
            if (Input.GetKey(KeyCode.Mouse0) && UseBrush(new Ray(transform.position, currentRotation * Vector3.forward), true))
            {
                // Activate the brush and increase the energy usage
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
                // Deactivate the brush and decrease the energy usage
                if (UsingBrush)
                {
                    UsingBrush = false;
                    laser1.gameObject.SetActive(false);
                    laser2.gameObject.SetActive(false);
                    EnergyUsage -= diggingEnergyUsage;
                }
            }
        }

        // Check if the game is not over and not won
        if (!GameOver && !GameWin)
        {
            // Update the energy usage and invoke the OnEnergyChange event
            Energy -= EnergyUsage / 20f * Time.fixedDeltaTime;
            OnEnergyChange.Invoke(Energy);

            // Check if the energy is depleted and lose the game if it is
            if (Energy <= 0)
            {
                Lose();
            }
        }

        // Deactivate the brush if the game is over or won
        if (GameOver || GameWin)
        {
            laser1.gameObject.SetActive(false);
            laser2.gameObject.SetActive(false);
        }
    }

    //Applies a brush operation on the terrain
    private bool UseBrush(Ray ray, bool eraseMode)
    {
        // Sample the current point in the map where the submarine is located
        float sample = map.SamplePoint(transform.position);

        // Check if the sample is less than the isolevel, meaning the submarine is below the surface
        if (sample < map.IsoLevel)
        {
            // Fire a ray to detect the surface
            if (map.FireRayParallel(ray, out float3 hit, 10f, map.BoundsSize / map.NumPointsPerAxis))
            {
                // Calculate the distance from the submarine to the hit point
                float distance = Vector3.Distance(transform.position, hit);

                // Check if the distance is within a valid range
                if (distance >= 0f && distance <= 10f)
                {
                    // Use a sphere brush to dig or erase the map
                    map.UseSphereBrush(hit, eraseMode, Time.fixedDeltaTime * (brushSpeed / 10f), new int3(1.5));

                    // Spawn particles at the hit point
                    DigParticles.Spawn(hit, Quaternion.identity);

                    // Play the mining sound and update the laser visuals if necessary
                    if (PlayMiningSound(hit))
                    {
                        UpdateLaser(hit, laser1, laserSprite1);
                        UpdateLaser(hit, laser2, laserSprite2);
                    }

                    // Return true to indicate that the brush was used successfully
                    return true;
                }
            }
        }

        // Return false to indicate that the brush was not used
        return false;
    }

    //Updates the laser position and rotation
    private void UpdateLaser(Vector3 target, Transform laser, SpriteRenderer sprite)
    {
        // Generate a random direction for the laser to point at
        Vector3 randomDirection = UnityEngine.Random.insideUnitSphere;

        // Calculate a point that is slightly offset from the target
        Vector3 point = target + (randomDirection * 0.5f);

        // Make the laser look at the point
        laser.LookAt(point);

        // Calculate the distance between the target and the laser
        float distance = length(point - laserSprite1.transform.position);

        // Adjust the laser sprite's scale and position based on the distance
        sprite.transform.localScale = new Vector3(1, 0.737f * distance, 1f);
        sprite.transform.localPosition = new Vector3(0f, 0f, 0.5f * distance);
    }

    private List<ContactPoint> contacts = new List<ContactPoint>(1); // Initialize a list of ContactPoints with a capacity of 1

    // This method is called when the submarine collides with another object
    private void OnCollisionEnter(Collision collision)
    {
        int numPoints = collision.GetContacts(contacts); // Get the number of contact points between the submarine and the collided object
        if (numPoints > 0) // If there is at least one contact point
        {
            float intensity = length(collision.impulse); // Calculate the intensity of the collision
            if (intensity > hitThreshold) // If the collision is strong enough to cause damage
            {
                TakeHit(contacts[0].point, intensity * damageMultiplier); // Call the TakeHit method to apply damage
            }
        }
    }

    // This method is called when the submarine takes damage
    private void TakeHit(Vector3 collisionPoint, float intensity)
    {
        Health -= intensity; // Decrease the submarine's health based on the intensity of the collision
        OnHit.Invoke(intensity); // Invoke the OnHit event with the intensity of the collision as a parameter
        audioSource.PlayOneShot(metalHitSound, (intensity / damageMultiplier / (hitThreshold * 1.5f))); // Play a metal hit sound with a pitch based on the intensity of the collision
        CrashParticles.Spawn(collisionPoint, Quaternion.identity); // Spawn particles at the collision point
        CrashParticles.Spawn(collisionPoint, Quaternion.identity); // Spawn particles at the collision point (again)

        if (Health <= 0f) // If the submarine's health is less than or equal to 0
        {
            Lose(); // Call the Lose method to end the game
        }
    }

    private float miningSoundTimer = 0f;

    // This method is called to play the mining sound when the submarine is mining
    private bool PlayMiningSound(Vector3 source)
    {
        miningSoundTimer -= Time.fixedDeltaTime; // Decrease the mining sound timer based on the fixed delta time
        if (miningSoundTimer <= 0) // If the mining sound timer has elapsed
        {
            miningSoundTimer += 1f / miningSoundFrequency; // Reset the mining sound timer based on the mining sound frequency

            AudioSource instance = AudioPool.PlayAtPoint(source, miningSound, mainMixerGroup); // Play the mining sound at the given source position
            instance.volume = miningSoundVolume; // Set the volume of the mining sound
            instance.pitch = UnityEngine.Random.Range(miningSoundPitchRange.x, miningSoundPitchRange.y); // Set the pitch of the mining sound randomly within the pitch range
            return true; // Return true to indicate that the mining sound was played
        }
        return false; // Return false to indicate that the mining sound was not played
    }

    private CircleTarget circleTargetInstance;
    private FindableObject previousNearestObject = null;

    //Updates the circle target graphic so that its hovering over a selected target
    private void UpdateTarget()
    {
        float nearestDistance = float.PositiveInfinity;
        FindableObject nearestObject = null;

        foreach (FindableObject obj in FindableObject.NearbyObjects)
        {
            float centeredness = dot(transform.rotation * Vector3.forward, (obj.transform.TransformPoint(obj.TargetOffset) - transform.position).normalized) + 1.01f;

            float len = (1f / centeredness) + (length(obj.transform.TransformPoint(obj.TargetOffset) - transform.position) / 10000f);    
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
        }

        if (nearestObject != null && circleTargetInstance == null)
        {
            circleTargetInstance = GameObject.Instantiate(circleTargetPrefab, UICanvas.transform);
            circleTargetInstance.TargetCanvas = UICanvas;
            circleTargetInstance.TargetCamera = mainCamera;
            circleTargetInstance.TargetObject = nearestObject;
        }


        previousNearestObject = nearestObject;

    }

    //Called when an object is found and collected
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

        ObjectsFound++;

        if (ObjectsFound >= ObjectsToFind)
        {
            Win();
        }
    }

    //Starts the game
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

    //Used to wait a set amount of time
    private IEnumerator Wait(float time, Action onDone)
    {
        yield return new WaitForSeconds(time);
        onDone?.Invoke();
    }

    //A cache used for storing plane data for IsVisibleFrom and IsTargetVisible
    private static Plane[] planeCache = new Plane[6];

    //Checks if the bounds is visible from the camera
    private static bool IsVisibleFrom(Bounds bounds, Camera camera)
    {
        GeometryUtility.CalculateFrustumPlanes(camera, planeCache);
        return GeometryUtility.TestPlanesAABB(planeCache, bounds);
    }

    //Checks if a target is visible in within the camera
    private bool IsTargetVisible(FindableObject target)
    {
        return IsVisibleFrom(new Bounds(target.transform.position, target.transform.localScale), mainCamera);
    }
}
