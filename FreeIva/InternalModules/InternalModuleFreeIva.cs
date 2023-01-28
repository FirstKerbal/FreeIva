﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Profiling;

namespace FreeIva
{
	public class InternalModuleFreeIva : InternalModule
	{
		#region Cache
		private static Dictionary<InternalModel, InternalModuleFreeIva> perModelCache = new Dictionary<InternalModel, InternalModuleFreeIva>();
		public static InternalModuleFreeIva GetForModel(InternalModel model)
		{
			if (model == null) return null;
			perModelCache.TryGetValue(model, out InternalModuleFreeIva module);
			return module;
		}

		public static void RefreshDepthMasks()
		{
			bool internalModeActive = CameraManager.Instance.currentCameraMode != CameraManager.CameraMode.Flight;

			foreach (var ivaModule in perModelCache.Values)
			{
				if (ivaModule.externalDepthMask == ivaModule.internalDepthMask && ivaModule.externalDepthMask != null)
				{
					ivaModule.externalDepthMask.gameObject.SetActive(true);
					continue;
				}

				if (ivaModule.externalDepthMask != null)
				{
					ivaModule.externalDepthMask.gameObject.SetActive(!internalModeActive);
				}

				if (ivaModule.internalDepthMask != null)
				{
					ivaModule.internalDepthMask.gameObject.SetActive(internalModeActive);
				}
			}
		}

		public static void RefreshInternals()
		{
			foreach (var ivaModule in perModelCache.Values)
			{
				if (ivaModule.vessel != FlightGlobals.ActiveVessel)
				{
					ivaModule.part.DespawnIVA();
				}
				else
				{
					foreach (var hatch in ivaModule.Hatches)
					{
						var otherHatch = hatch.ConnectedHatch;
						if (otherHatch == null || otherHatch.vessel != hatch.vessel)
						{
							hatch.Open(false, false);
						}
						hatch.RefreshConnection();
					}
				}
			}
		}

		#endregion

		[KSPField]
		public bool CopyPartCollidersToInternalColliders = false;

		public List<FreeIvaHatch> Hatches = new List<FreeIvaHatch>(); // hatches will register themselves with us

		List<CutParameter> cutParameters = new List<CutParameter>();
		int propCutsRemaining = 0;

		[KSPField]
		public string secondaryInternalName = string.Empty;
		public InternalModel SecondaryInternalModel { get; private set; }

		[KSPField]
		public string centrifugeTransformName = string.Empty;
		[KSPField]
		public Vector3 centrifugeAlignmentRotation = new Vector3(180, 0, 180);

		public ICentrifuge Centrifuge { get; private set; }
		public IDeployable Deployable { get; private set; }

		public bool NeedsDeployable;
		[KSPField]
		public string deployAnimationName = string.Empty;

		[KSPField]
		public string externalDepthMaskFile = string.Empty;
		[KSPField]
		public string externalDepthMaskName = string.Empty;
		public Transform externalDepthMask;

		[KSPField]
		public string internalDepthMaskName = string.Empty;
		public Transform internalDepthMask;

		[SerializeField]
		public Bounds ShellColliderBounds;

		public override void OnLoad(ConfigNode node)
		{
			base.OnLoad(node);

			// Find the bounds of the shell colliders, so that we can tell when the player exits the bounds of a centrifuge
			ShellColliderBounds.center = Vector3.zero;
			ShellColliderBounds.size = -Vector3.one;
			foreach (var shellColliderName in node.GetValues("shellColliderName"))
			{
				var transform = TransformUtil.FindInternalModelTransform(internalModel, shellColliderName);
				if (transform != null)
				{
					var colliders = transform.GetComponentsInChildren<MeshCollider>();
					foreach (var meshCollider in colliders)
					{
						ShellColliderBounds.Encapsulate(meshCollider.bounds);
						meshCollider.convex = false;
					}

					if (colliders.Length == 0)
					{
						Debug.LogError($"[FreeIva] shellCollider {shellColliderName} in internal {internalModel.internalName} exists but does not have a MeshCollider");
					}
				}
				else
				{
					Debug.LogError($"[FreeIva] shellCollider {shellColliderName} not found in internal {internalModel.internalName}");
				}
			}

			if (CopyPartCollidersToInternalColliders)
			{
				var partBoxColliders = GetComponentsInChildren<BoxCollider>();

				if (partBoxColliders.Length > 0)
				{
					foreach (var c in partBoxColliders)
					{
						if (c.isTrigger || c.tag != "Untagged")
						{
							continue;
						}

						var go = Instantiate(c.gameObject);
						go.transform.parent = internalModel.transform;
						go.layer = (int)Layers.Kerbals;
						go.transform.position = InternalSpace.WorldToInternal(c.transform.position);
						go.transform.rotation = InternalSpace.WorldToInternal(c.transform.rotation);
					}
				}
			}

			var disableColliderNode = node.GetNode("DisableCollider");
			if (disableColliderNode != null)
			{
				DisableCollider.DisableColliders(internalProp, disableColliderNode);
			}

			var deleteObjectsNode = node.GetNode("DeleteInternalObject");
			if (deleteObjectsNode != null)
			{
				DeleteInternalObject.DeleteObjects(internalProp, deleteObjectsNode);
			}

			foreach (var reparentNode in node.GetNodes("Reparent"))
			{
				ReparentUtil.Reparent(internalProp, reparentNode);
			}

			OnLoad_DepthMasks();
			bool hasWindows = OnLoad_Windows(node);
			OnLoad_MeshCuts(node);

			if (internalDepthMask == null && !hasWindows)
			{
				Debug.LogWarning($"[FreeIva] INTERNAL '{internalModel.internalName}' has neither an internal depth mask nor detectable windows.  It may be possible to see the internals of other parts from here.");
			}
		}

		private void OnLoad_DepthMasks()
		{
			if (internalDepthMaskName != string.Empty)
			{
				internalDepthMask = TransformUtil.FindInternalModelTransform(internalModel, internalDepthMaskName);
			}

			if (externalDepthMaskName != string.Empty)
			{
				externalDepthMask = TransformUtil.FindInternalModelTransform(internalModel, externalDepthMaskName);
			}
			else if (externalDepthMaskFile != string.Empty)
			{
				externalDepthMask = TransformUtil.FindModelFile(internalModel.gameObject.transform, externalDepthMaskFile);
			}

			// try to find the external depth mask from existing meshes, based on the shader
			if (externalDepthMask == null)
			{
				var modelTransform = internalModel.gameObject.transform.Find("model");
				if (modelTransform != null)
				{
					// find all the depth mask renderers that aren't a child of the internal depth mask (if there is one)
					var allRenderers = modelTransform.GetComponentsInChildren<MeshRenderer>();

					var rendererGroups = allRenderers.GroupBy(meshRenderer =>
						meshRenderer.material.shader == Utils.GetDepthMask() &&
						(internalDepthMask == null || !meshRenderer.transform.IsChildOf(internalDepthMask)));

					var depthMaskRenderers = rendererGroups.Where(group => group.Key).FirstOrDefault();

					if (depthMaskRenderers != null && depthMaskRenderers.Any())
					{
						externalDepthMask = depthMaskRenderers.First().transform;
						// need to find the common ancestor of all the depth mask renderers
						foreach (var renderer in depthMaskRenderers)
						{
							renderer.gameObject.layer = (int)Layers.InternalSpace;

							while (!renderer.transform.IsChildOf(externalDepthMask))
							{
								externalDepthMask = externalDepthMask.parent;
							}
						}

						// if any of the OTHER renderers are also a child of this transform, we can't use it
						var otherRenderers = rendererGroups.Where(group => !group.Key).FirstOrDefault();
						if (otherRenderers != null)
						{
							if (otherRenderers.Any(otherRenderer => otherRenderer.transform.IsChildOf(externalDepthMask)))
							{
								Debug.LogWarning($"[FreeIva] INTERNAL '{internalModel.internalName}' auto-detected depth mask common ancestor '{externalDepthMask.name}' also has non-depth-mask renderers; cannot be used");
								externalDepthMask = null;
							}
						}
						else
						{
							Debug.Log($"[FreeIva] INTERNAL '{internalModel.internalName}' auto-detected external depth mask transform '{externalDepthMask.name}'");
						}
					}
					else
					{
						Debug.LogWarning($"[FreeIva] could not auto-detect depth mask for INTERNAL '{internalModel.internalName}' - no matching MeshRenderers found");
					}
				}
			}

			// try to generate an internal depth mask from the internal geometry
#if false
			if (internalDepthMask == null)
			{
				var stopwatch = new System.Diagnostics.Stopwatch();
				stopwatch.Start();
				Profiler.BeginSample("DepthMaskHull");

				var convexHullCalculator = new GK.ConvexHullCalculator();

				var currentVertices = new List<Vector3>();
				var modelTransform = internalModel.transform.Find("model");

				foreach (var meshRenderer in modelTransform.GetComponentsInChildren<MeshRenderer>())
				{
					var meshFilter = meshRenderer.GetComponent<MeshFilter>();

					Vector3 meshPosition = modelTransform.InverseTransformPoint(meshRenderer.transform.position);
					Quaternion meshRotation = Quaternion.Inverse(modelTransform.rotation) * meshRenderer.transform.rotation;

					if (meshPosition == Vector3.zero && Quaternion.Dot(meshRotation, Quaternion.identity) > 0.999f)
					{
						currentVertices.AddRange(meshFilter.mesh.vertices);
					}
					else
					{
						currentVertices.Capacity += meshFilter.mesh.vertices.Length;
						foreach (var vertex in meshFilter.mesh.vertices)
						{
							currentVertices.Add(meshRotation * vertex + meshPosition);
						}
					}
				}

				List<Vector3> newVerts = null;
				List<int> newIndices = null;
				List<Vector3> newNormals = null;

				if (currentVertices.Count > 0)
				{
					try
					{
						convexHullCalculator.GenerateHull(currentVertices, false, ref newVerts, ref newIndices, ref newNormals);

						var newMesh = new Mesh();
						newMesh.vertices = newVerts.ToArray();
						newMesh.triangles = newIndices.ToArray();

						internalDepthMask = new GameObject("InternalDepthMask").transform;
						internalDepthMask.SetParent(internalModel.transform, false);
						// internalDepthMask.localScale = Vector3.one * 1.001f;
						internalDepthMask.gameObject.AddComponent<MeshFilter>().mesh = newMesh;
						internalDepthMask.gameObject.AddComponent<MeshRenderer>();
						internalDepthMask.gameObject.layer = (int)Layers.InternalSpace;
					}
					catch (Exception ex)
					{
						Debug.LogException(ex);
					}
				}

				Profiler.EndSample();
				stopwatch.Stop();

				if (internalDepthMask != null)
				{
					Debug.Log($"[FreeIVA] depth mask convex hull for {internalModel.internalName}; {newVerts.Count} verts; {newIndices.Count / 3} triangles; {stopwatch.Elapsed.TotalMilliseconds}ms");
				}
			}
#endif

			if (internalDepthMask != null)
			{
				foreach (var meshRenderer in internalDepthMask.GetComponentsInChildren<MeshRenderer>())
				{
					meshRenderer.sharedMaterial = Utils.GetDepthMaskCullingMaterial();
					meshRenderer.gameObject.layer = (int)Layers.InternalSpace;
					meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
					meshRenderer.receiveShadows = false;
				}
			}
		}

		static Dictionary<Shader, Shader> x_windowShaderTranslations = null;
		static Shader[] x_windowShaders = null;

		// transparents are normally 3000, opaque geometry is 2000
		// we need something that will render before opaque geometry so that it writes to the z-buffer early and prevents other internals from drawing behind it
		public static readonly int WINDOW_RENDER_QUEUE = 1999;

		private bool OnLoad_Windows(ConfigNode node)
		{
			bool hasWindows = false;

			if (x_windowShaderTranslations == null)
			{
				// for transforms specified as windows, we might need to change their shader to one that does z-write
				x_windowShaderTranslations = new Dictionary<Shader, Shader>()
				{
					{Shader.Find("Unlit/Transparent"), Shader.Find("KSP/Alpha/Unlit Transparent")},
				};
			}

			if (x_windowShaders == null)
			{
				x_windowShaders = new Shader[]
				{
					Shader.Find("KSP/Alpha/Translucent Specular"),
					Shader.Find("KSP/Alpha/Translucent"),
					Shader.Find("KSP/Alpha/Unlit Transparent"),
				};
			}

			var windowNames = node.GetValues("windowName");
			foreach (var windowName in windowNames)
			{
				var windowTransform = TransformUtil.FindInternalModelTransform(internalModel, windowName);
				if (windowTransform != null)
				{
					foreach (var meshRenderer in windowTransform.GetComponentsInChildren<MeshRenderer>())
					{
						if (x_windowShaderTranslations.TryGetValue(meshRenderer.material.shader, out Shader newShader))
						{
							meshRenderer.material.shader = newShader;
						}

						meshRenderer.material.renderQueue = WINDOW_RENDER_QUEUE;
					}

					hasWindows = true;
				}
			}

			// if there aren't any window names specified, try to find them by shader (unless we have an internal depth mask)
			if (!windowNames.Any() && internalDepthMask == null)
			{
				var modelTransform = internalModel.transform.Find("model");
				foreach (var meshRenderer in modelTransform.GetComponentsInChildren<MeshRenderer>())
				{
					if (x_windowShaders.Contains(meshRenderer.material.shader))
					{
						hasWindows = true;
						meshRenderer.material.renderQueue = WINDOW_RENDER_QUEUE;
						meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
						Debug.Log($"[FreeIva] INTERNAL '{internalModel.internalName}' auto-detected window transform '{meshRenderer.transform.name}'");
					}
				}
			}

			return hasWindows;
		}

		private void OnLoad_MeshCuts(ConfigNode node)
		{
			var cutNodes = node.GetNodes("Cut");
			foreach (var cutNode in cutNodes)
			{
				CutParameter cp = CutParameter.LoadFromCfg(cutNode);

				if (cp != null)
				{
					cutParameters.Add(cp);
				}
			}

			// I can't find a better way to gather all the prop cuts and execute them at once for the entire IVA
			propCutsRemaining = CountPropCuts();

			if (propCutsRemaining == 0)
			{
				ExecuteMeshCuts();
			}
			else
			{
				// need to add cuts from props that are already loaded
				foreach (var prop in internalModel.props)
				{
					// since CountPropCuts counts all props with a HatchConfig module node, we need to call AddPropCut for each prop with a HatchConfig module
					var hatchConfig = prop.GetComponent<HatchConfig>();
					if (hatchConfig != null)
					{
						var hatchComponent = prop.GetComponent<FreeIvaHatch>();
						AddPropCut(hatchComponent);
					}
				}
			}
		}

		int CountPropCuts()
		{
			int count = 0;

			foreach (var propNode in internalModel.internalConfig.GetNodes("PROP"))
			{
				var propName = propNode.GetValue("name");
				var propPrefab = PartLoader.Instance.internalProps.FirstOrDefault(prop => prop.propName == propName);

				foreach (var moduleNode in propNode.GetNodes("MODULE"))
				{
					if (moduleNode.GetValue("name") == nameof(HatchConfig))
					{
						++count;
					}
				}
			}

			return count;
		}

		public void AddPropCut(FreeIvaHatch hatch)
		{
			if (hatch.cutoutTransformName != string.Empty)
			{
				var tool = TransformUtil.FindPropTransform(hatch.internalProp, hatch.cutoutTransformName);
				if (tool != null)
				{
					CutParameter cp = new CutParameter();
					cp.target = hatch.cutoutTargetTransformName;
					cp.tool = tool.gameObject;
					cp.type = CutParameter.Type.Mesh;
					cutParameters.Add(cp);
				}
				else
				{
					Debug.LogError($"[FreeIva] could not find cutout transform {hatch.cutoutTransformName} on prop {hatch.internalProp.propName}");
				}
			}

			if (--propCutsRemaining == 0)
			{
				ExecuteMeshCuts();
			}
		}

		InternalModel CreateInternalModel(string internalName)
		{
			InternalModel internalPart = PartLoader.GetInternalPart(internalName);
			if (internalPart == null)
			{
				Debug.LogError($"[FreeIva] Could not find INTERNAL named '{internalName}' referenced from INTERNAL '{internalModel.name}'");
				return null;
			}
			var result = UnityEngine.Object.Instantiate(internalPart);
			result.gameObject.name = internalPart.internalName + " interior";
			result.gameObject.SetActive(value: true);
			if (result == null)
			{
				Debug.LogError($"[FreeIva] Failed to instantiate INTERNAL named '{internalName}' referenced from INTERNAL '{internalModel.name}'");
				return null;
			}
			result.part = part;
			result.Load(new ConfigNode());
			
			// InternalModule.Initialize will try to seat the crew, but we don't want to do that for secondary internal models
			var partCrew = part.protoModuleCrew;
			part.protoModuleCrew = new List<ProtoCrewMember>();
			result.Initialize(part);
			part.protoModuleCrew = partCrew;
			return result;
		}

		void Start()
		{
			if (!HighLogic.LoadedSceneIsFlight) return;

			if (secondaryInternalName != string.Empty)
			{
				SecondaryInternalModel = CreateInternalModel(secondaryInternalName);
			}

			// the rotating part of the centrifuge has a secondary internal (which is the stationary part)
			// for now we'll only set up the centrifuge module on the rotating part
			if (SecondaryInternalModel != null || centrifugeTransformName != string.Empty)
			{
				Centrifuge = CentrifugeFactory.Create(part, centrifugeTransformName, centrifugeAlignmentRotation);
				Deployable = Centrifuge as IDeployable; // some centrifuges may also be deployables
			}

			if (NeedsDeployable && Deployable == null)
			{
				Deployable = DeployableFactory.Create(part, deployAnimationName);

				if (Deployable == null)
				{
					Debug.LogError($"[FreeIva] Could not find a module to handle deployment in INTERNAL '{internalModel.internalName}' for PART '{part.partInfo.name}'");
				}
			}
		}

		void Update()
		{
			this.internalModel.transform.position = InternalSpace.WorldToInternal(part.transform.position);
			this.internalModel.transform.rotation = InternalSpace.WorldToInternal(part.transform.rotation) * Quaternion.Euler(90, 0, 180);

			if (Centrifuge != null)
			{
				Centrifuge.Update();
			}
		}

		void ExecuteMeshCuts()
		{
			if (HighLogic.LoadedScene != GameScenes.LOADING) return;

			if (cutParameters.Any())
			{
				MeshCutter.CreateToolsForCutParameters(internalModel, cutParameters);
				MeshCutter.CutInternalModel(internalModel, cutParameters);


// this code is only necessary for the internal depth masks that were generated via convex hull.  We're not doing that anymore
#if false
				if (internalDepthMask != null)
				{
					MeshCutter.ApplyCut(internalDepthMask, cutParameters);

					var mesh = internalDepthMask.GetComponent<MeshFilter>().mesh;

					Debug.Log($"[FreeIva] after cutting internal depth mask: {mesh.vertices.Length} verts; {mesh.triangles.Length / 3} tris");
				}
#endif
			}

			MeshCutter.DestroyTools(ref cutParameters);
		}

		new void Awake()
		{
			if (HighLogic.LoadedScene == GameScenes.FLIGHT)
			{
				perModelCache[internalModel] = this;
			}
			base.Awake();
		}

		void OnDestroy()
		{
			perModelCache.Remove(internalModel);
			if (SecondaryInternalModel != null)
			{
				GameObject.Destroy(SecondaryInternalModel.gameObject);
			}
		}
	}
}
