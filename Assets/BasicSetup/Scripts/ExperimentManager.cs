﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ExperimentManager : MonoBehaviour
{

    /**
     * Note: Scene_00 is the default waiting scene. It will only move
     *       onto the next scene when setup is complete.
     */

    [Header("Scene Settings")]
    [Tooltip("Specify how many total scenes are there total")]
    public uint totalNumberOfScenes;
    [Tooltip("Number of seconds to stay on each scene")]
    public uint secondsBetweenScene;
    [Tooltip("The fade out transition duration in seconds")]
    public uint fadeOutDuration;
    [Tooltip("The cooldown time between manually requesting scene change in seconds")]
    public uint sceneChangeCooldownDuration = 1;

    [Header("Required Linked Components")]
    [Tooltip("Link to an instance of the eye tracking's script")]
    public EyeTracker eyeTrackerInstance;
    [Tooltip("Instance to script controlling camera opacity")]
    public CameraFade cameraFadeInstance;
    [Tooltip("Instance to headset localization script")]
    public UpdateFoveFromViveTracker headsetLocalizationInstance;
    [Tooltip("Instance to UI Prompt for Experiment Manager")]
    public ExperimentManagerUiPrompt uiPrompt;
    [Tooltip("Instance of reflection probe")]
    public ReflectionProbe reflectionProbeInstance;
    [Tooltip("Get a game object instance of the headset for position/orientation data")]
    public GameObject cameraGameObject;

    [Header("Skybox Specifications")]
    [Tooltip("Set texture directory")]
    public string textureDirectory = "Assets/SkyBox";
    [Tooltip("Default texture if cannot find specified")]
    public Material defaultSkyboxMaterial;
    [Tooltip("Set the skybox texture to know what to change")]
    public Material skyboxMaterial;

    private enum SCENE_CHANGE_STATE
    {
        NotChanging,
        FadeOut,
        InitiateChange,
        Changing,
        FadeIn
    }

    private bool systemCalibrated = false;
    private bool eyeTrackerCalibrationStarted = false;

    private int currentSceneNumber;
    private long nextSceneTime = long.MaxValue;
    private long fadeSceneTime = long.MaxValue;
    private long nextAllowedSceneChangeRequestTime = 0;
    private SCENE_CHANGE_STATE currentSceneState;

    private Vector3 scenePositionShiftAmount;

    private Queue fpsTracker;
    private long lastUpdateTimeNs;
    private double fps;

    private bool skipIntro = false;

    // Use this for initialization
    void Start()
    {
        fpsTracker = new Queue();
        lastUpdateTimeNs = DateTime.Now.Ticks;
        fps = 0;

        // Initialize variables
        systemCalibrated = false;
        eyeTrackerCalibrationStarted = false;
        currentSceneNumber = 0;
        currentSceneState = SCENE_CHANGE_STATE.NotChanging;
        nextAllowedSceneChangeRequestTime = 0;

        // Unload all scenes
        int totalScenesKnown = SceneManager.sceneCount;
        for (int i = 0; i < totalScenesKnown; i++)
        {
            Scene scene = SceneManager.GetSceneByBuildIndex(i);
            if (scene.isLoaded && scene.name != "Default")
                SceneManager.UnloadSceneAsync(scene.name);
        }

        nextSceneTime = long.MaxValue;

        if(skipIntro)
            systemCalibrated = true;
    }

    // Update is called once per frame
    void Update()
    {
        // Ensure everything is calibrated before starting experiment
        if (systemCalibrated == false)
        {
            RunCalibrationSetup();
            return;
        }

        if(skipIntro && (currentSceneNumber == 0))
        {
            currentSceneNumber = 1;
            uiPrompt.TurnOffUiPrompt();
            eyeTrackerInstance.CreateNewScene(GenerateSceneName(currentSceneNumber));
            SceneManager.LoadSceneAsync(GenerateSceneName(currentSceneNumber), LoadSceneMode.Additive);
            ChangeSkybox(currentSceneNumber);
            currentSceneState = SCENE_CHANGE_STATE.NotChanging;
            nextSceneTime = DateTime.Now.Ticks + secondsBetweenScene * TimeSpan.TicksPerSecond;
        }

        // Check if we should begin transition onto the next scene
        if (DateTime.Now.Ticks >= nextSceneTime || currentSceneState != SCENE_CHANGE_STATE.NotChanging)
        {
            switch (currentSceneState)
            {
                // Initiate change scene if we're not at the last scene
                case SCENE_CHANGE_STATE.NotChanging:
                    if (currentSceneNumber < totalNumberOfScenes)
                    {
                        eyeTrackerInstance.SaveToFile();
                        fadeSceneTime = nextSceneTime;
                        currentSceneState = SCENE_CHANGE_STATE.FadeOut;
                    }
                    break;
                
                // Fade scene out
                case SCENE_CHANGE_STATE.FadeOut:
                    float fadeOutOpacity = 1.0f - ((float)(DateTime.Now.Ticks - fadeSceneTime)) / ((float)(fadeOutDuration * TimeSpan.TicksPerSecond));
                    fadeOutOpacity = Math.Max(0.0f, fadeOutOpacity);
                    cameraFadeInstance.setOpacity(fadeOutOpacity);
                    if (fadeOutOpacity == 0.0f)
                        currentSceneState = SCENE_CHANGE_STATE.InitiateChange;
                    break;
                
                // Tell program to start changing scene
                case SCENE_CHANGE_STATE.InitiateChange:
                    SceneManager.UnloadSceneAsync(GenerateSceneName(currentSceneNumber));
                    currentSceneNumber++;
                    if (currentSceneNumber < totalNumberOfScenes)
                    {
                        SceneManager.LoadSceneAsync(GenerateSceneName(currentSceneNumber), LoadSceneMode.Additive);
                        ChangeSkybox(currentSceneNumber);
                        currentSceneState = SCENE_CHANGE_STATE.Changing;
                    }
                    else
                    {
                        ChangeSkybox(-1);
                        uiPrompt.SetUiPromptMessage("Session has ended.");
                        uiPrompt.TurnOnUiPrompt();
                        fadeSceneTime = DateTime.Now.Ticks;
                        currentSceneState = SCENE_CHANGE_STATE.FadeIn;
                    }
                    break;

                // Wait for scene to finish loading
                case SCENE_CHANGE_STATE.Changing:
                    if (SceneManager.GetSceneByName(GenerateSceneName(currentSceneNumber)).isLoaded)
                    {
                        fadeSceneTime = DateTime.Now.Ticks;
                        currentSceneState = SCENE_CHANGE_STATE.FadeIn;
                    }
                    break;
                
                // Fade back in
                case SCENE_CHANGE_STATE.FadeIn:
                    float fadeInOpacity = ((float)(DateTime.Now.Ticks - fadeSceneTime)) / ((float)(fadeOutDuration * TimeSpan.TicksPerSecond));
                    fadeInOpacity = Math.Min(1.0f, fadeInOpacity);
                    cameraFadeInstance.setOpacity(fadeInOpacity);
                    if (fadeInOpacity == 1.0f)
                    {
                        eyeTrackerInstance.CreateNewScene(GenerateSceneName(currentSceneNumber));
                        currentSceneState = SCENE_CHANGE_STATE.NotChanging;
                        if(nextSceneTime <= DateTime.Now.Ticks)
                            nextSceneTime = DateTime.Now.Ticks + secondsBetweenScene * TimeSpan.TicksPerSecond;
                    }
                    break;
            }
        }

        if (Input.GetKeyDown(KeyCode.N))
            ChangeToNextScene();

        // For debugging purposes, keep track of FPS
        long timeBetweenFrameNs = DateTime.Now.Ticks - lastUpdateTimeNs;
        fps += (double) TimeSpan.TicksPerSecond / (double) timeBetweenFrameNs / 100.0;
        fpsTracker.Enqueue(timeBetweenFrameNs);
        if( fpsTracker.Count > 100 )
            fps -= (double)TimeSpan.TicksPerSecond / (double)(long) fpsTracker.Dequeue() / 100.0;
        lastUpdateTimeNs = DateTime.Now.Ticks;
        //Debug.Log("FPS: " + fps );
    }

    private void RunCalibrationSetup()
    {
        if (eyeTrackerCalibrationStarted == false)
        {
            FoveInterface.EnsureEyeTrackingCalibration();
            eyeTrackerCalibrationStarted = true;
        }

        if (RenderSettings.skybox != defaultSkyboxMaterial)
            RenderSettings.skybox = defaultSkyboxMaterial;

        bool eyeTrackerCalibrated = (!FoveInterface.IsEyeTrackingCalibrating() && FoveInterface.IsEyeTrackingCalibrated());
        bool headsetCalibrated = headsetLocalizationInstance.IsHeadsetCalibrated();
        bool viveTrackerHeadsetConnected = headsetLocalizationInstance.IsHeadsetPositionTracked();
        bool controllersConnected = headsetLocalizationInstance.IsControllersConnected();

        string uiPromptMessage = "";
        if (viveTrackerHeadsetConnected == false) uiPromptMessage += "Please turn on Vive Tracker for headset.\n";
        if (controllersConnected == false) uiPromptMessage += "Please connect both Vive controllers.\n";
        if (eyeTrackerCalibrated == false) uiPromptMessage += "Please calibrate eye tracking.\n";
        if (headsetCalibrated == false) uiPromptMessage += "Please calibrate headset and vive to world space [H]\n";
        uiPrompt.SetUiPromptMessage(uiPromptMessage);
        uiPrompt.TurnOnUiPrompt();

        if (eyeTrackerCalibrated && headsetCalibrated)
        {
            systemCalibrated = true;
            uiPrompt.TurnOffUiPrompt();

            // Initialize a new scene
            headsetLocalizationInstance.ReCenterUser();
            eyeTrackerInstance.CreateNewScene(GenerateSceneName(currentSceneNumber));
            SceneManager.LoadSceneAsync(GenerateSceneName(currentSceneNumber), LoadSceneMode.Additive);
            ChangeSkybox(currentSceneNumber);
        }
    }

    private void ChangeSkybox(int sceneNumber)
    {
        Debug.Log("Loading in scene " + sceneNumber); ;

        // Set universal skybox
        Cubemap newSkyBox = Resources.Load<Cubemap>(GenerateSceneName(currentSceneNumber));
        if (newSkyBox != null)
        {
            skyboxMaterial.SetTexture("_Tex", newSkyBox);
            RenderSettings.skybox = skyboxMaterial;
        }
        else
            RenderSettings.skybox = defaultSkyboxMaterial;

        // Set stereoscopic skybox -- left is default skybox. Change right skybox only 
        /*
        if (sceneNumber >= 0)
        {
            if (GameObject.Find("FOVE Eye (Right)").GetComponent<Skybox>() == null)
                GameObject.Find("FOVE Eye (Right)").AddComponent<Skybox>();
            Camera camRight = GameObject.Find("FOVE Eye (Right)").GetComponent<Camera>();
            camRight.clearFlags = CameraClearFlags.Skybox;
            camRight.GetComponent<Skybox>().material = defaultSkyboxMaterial;// Resources.Load("Material/New_Material", typeof(Material)) as Material;
        }
        else
        {
            if (GameObject.Find("FOVE Eye (Right)").GetComponent<Skybox>() != null)
                Destroy(GameObject.Find("FOVE Eye (Right)").GetComponent<Skybox>());
        }*/

        // Update reflection probe
        reflectionProbeInstance.RenderProbe();
    }

    private string GenerateSceneName(int sceneNumber)
    {
        return "Scene_" + sceneNumber.ToString("000");
    }

    public void ChangeToNextScene()
    {
        if (nextAllowedSceneChangeRequestTime < DateTime.Now.Ticks)
        {
            if (currentSceneState == SCENE_CHANGE_STATE.NotChanging)
            {
                nextSceneTime = DateTime.Now.Ticks;
                nextAllowedSceneChangeRequestTime = DateTime.Now.Ticks + sceneChangeCooldownDuration * TimeSpan.TicksPerSecond;
            }
        }
    }

    public Transform getUserHeadTransform()
    {
        return cameraGameObject.transform;
    }



    /**
     * This is to toggle automatic scene change for the *current* scene only.
     * If this function is not called, it is default to TRUE for automatic scene change.
     */
    public void ToggleAutoSceneChange(bool automatic)
    {
        if (automatic)
            nextSceneTime = DateTime.Now.Ticks + secondsBetweenScene * TimeSpan.TicksPerSecond;
        else
            nextSceneTime = long.MaxValue;
    }

    public void ToggleEyeCursor(bool show)
    {
        eyeTrackerInstance.renderCursor = show;
    }

    public SteamVR_Controller.Device [] GetControllers()
    {
        return headsetLocalizationInstance.GetControllers();
    }
}
