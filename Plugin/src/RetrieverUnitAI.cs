using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace RetrieverUnit
{

    class RetrieverUnitAI : EnemyAI
    {

#pragma warning disable 0649
        public Transform itemHoldPoint = null!;
        public AudioSource CarAmbientAudio = null!;
        public AudioClip idleHumSFX = null!;
        public AudioClip activateSFX = null!;
        public AudioClip pickupSFX = null!;
        public AudioClip dropSFX = null!;
        public AudioClip errorSFX = null!;
        public Light CarLight = null!;
#pragma warning restore 0649

        [Header("Retriever Settings")]
        public float activeSpeed = 10f;
        public float itemStuckTime = 8f;
        public float maxItemHeightDiff = 6f;
        public float lockpickerPlaceRadius = 12f;

        bool isTeleporting;
        bool isPickingUp;
        bool isDropping;
        bool isParked;
        bool isPlacingLockpicker;
        bool isExploding;
        bool lockRotation;

        GrabbableObject carriedItem = null!;
        GrabbableObject targetItem = null!;
        GrabbableObject heldLockpicker = null!;
        bool carriedItemIsKey;

        EntranceTeleport usedOutsideEntrance = null!;
        EntranceTeleport usedInsideExit = null!;

        List<EntranceTeleport> reachableOutsideEntrances = new List<EntranceTeleport>();
        List<EntranceTeleport> reachableInsideExits = new List<EntranceTeleport>();
        List<EntranceTeleport> allOutsideEntrances = new List<EntranceTeleport>();
        List<EntranceTeleport> allInsideExits = new List<EntranceTeleport>();

        EntranceTeleport chosenEntrance = null!;
        EntranceTeleport chosenInsideExit = null!;

        Vector3 cachedShipPos;
        Vector3 smoothedForward;

        float stuckOnItemTimer;
        Vector3 lastPosCheck;
        float lastPosCheckTime;

        int emptySearchCount = 0;
        const int MAX_EMPTY_SEARCHES = 2;

        string CarName = "Retriever";
        System.Random CarRandom = null!;

        enum State
        {
            Init,
            GoingToEntrance,
            GoingToItem,
            GoingToExit,
            GoingToShip,
            Dropping,
            PlacingLockpicker,
            GoingToLockedDoor,
            Parked,
        }

        void LogIfDebugBuild(string text)
        {
            Plugin.Logger.LogInfo(text);
        }

        public override void Start()
        {
            base.Start();

            // We only run updateRotation through our own AlignToSurface method,
            // so we disable the agent's built-in rotation to avoid conflicts.
            agent.updateRotation = false;
            agent.updatePosition = true;
            smoothedForward = transform.forward;

            CarRandom = new System.Random(StartOfRound.Instance.randomMapSeed + thisEnemyIndex);
            CarName = GenerateCarName();

            activeSpeed = Plugin.BoundConfig.MoveSpeed.Value;
            agent.speed = activeSpeed;
            agent.angularSpeed = activeSpeed * 30f;
            agent.acceleration = activeSpeed * 8f;

            if (CarAmbientAudio != null && idleHumSFX != null)
            {
                CarAmbientAudio.clip = idleHumSFX;
                CarAmbientAudio.loop = true;
                CarAmbientAudio.volume = 0.4f;
                CarAmbientAudio.spatialBlend = 1f;
                CarAmbientAudio.Play();
            }

            PlaySound(activateSFX);
            SetupScanNode();
            RegisterAsRadarTarget();

            // NOTE: We start in Init state and wait for NavMesh to be ready before doing anything.
            // This avoids issues where the agent tries to path before the mesh is loaded.
            currentBehaviourStateIndex = (int)State.Init;
            StartCoroutine(InitRoutine());
        }

        string GenerateCarName()
        {
            string[] prefixes = { "UNIT", "BOT", "Car", "RET", "MK" };
            string[] suffixes = { "Alpha", "Beta", "Gamma", "Delta", "Echo",
                                  "Foxtrot", "Sigma", "Omega", "Prime", "Zero" };
            return $"{prefixes[CarRandom.Next(0, prefixes.Length)]}" +
                   $"-{CarRandom.Next(1, 99):D2}" +
                   $"-{suffixes[CarRandom.Next(0, suffixes.Length)]}";
        }

        public override void Update()
        {
            base.Update();
            AlignToSurface();

            // We keep the carried item glued to the hold point every frame since
            // the item's own physics would otherwise push it away from the Car.
            if (carriedItem != null && itemHoldPoint != null)
            {
                carriedItem.transform.position = itemHoldPoint.position;
                carriedItem.transform.rotation = itemHoldPoint.rotation;
            }
        }

        // i hate this part of the code
        void AlignToSurface()
        {
            Vector3 velocity = agent.velocity;
            velocity.y = 0f;

            if (velocity.sqrMagnitude > 0.1f)
                smoothedForward = Vector3.Lerp(smoothedForward, velocity.normalized, Time.deltaTime * 8f);

            Vector3 forward = smoothedForward.sqrMagnitude > 0.01f
                ? smoothedForward
                : transform.forward;

            if (!Physics.Raycast(
                    transform.position + Vector3.up * 0.3f,
                    Vector3.down,
                    out RaycastHit hit,
                    1.5f,
                    StartOfRound.Instance.collidersAndRoomMaskAndDefault,
                    QueryTriggerInteraction.Ignore))
            {
                Quaternion upright = Quaternion.LookRotation(forward, Vector3.up);
                transform.rotation = Quaternion.Slerp(transform.rotation, upright, Time.deltaTime * 5f);
                return;
            }

            Vector3 normal = hit.normal;
            if (Vector3.Dot(normal, Vector3.up) < 0.3f) return;

            Vector3 projectedForward = Vector3.ProjectOnPlane(forward, normal);
            if (projectedForward.sqrMagnitude < 0.01f)
                projectedForward = Vector3.ProjectOnPlane(Vector3.forward, normal);
            projectedForward.Normalize();

            Quaternion targetRot = Quaternion.LookRotation(projectedForward, normal);
            float angle = Quaternion.Angle(transform.rotation, targetRot);
            float speed = Mathf.Lerp(5f, 15f, angle / 45f);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, Time.deltaTime * speed);
        }

        // We register the Car as a radar target so it appears on the ship monitor,
        // its still a bit buggy and not syncing properly in multiplayer
        void RegisterAsRadarTarget()
        {
            ManualCameraRenderer mapScreen = StartOfRound.Instance?.mapScreen;
            if (mapScreen == null) return;
            try
            {
                var list = mapScreen.radarTargets;
                var type = list[0].GetType();
                var instance = System.Activator.CreateInstance(type, transform, CarName, false);
                list.Add((TransformAndName)instance);
            }
            catch (System.Exception e) { LogIfDebugBuild($"Radar register failed: {e.Message}"); }
        }

        void UnregisterRadarTarget()
        {
            ManualCameraRenderer mapScreen = StartOfRound.Instance?.mapScreen;
            if (mapScreen == null) return;
            for (int i = mapScreen.radarTargets.Count - 1; i >= 0; i--)
                if (mapScreen.radarTargets[i]?.transform == transform)
                { mapScreen.radarTargets.RemoveAt(i); break; }
        }

        void SetupScanNode()
        {
            var node = GetComponentInChildren<ScanNodeProperties>();
            if (node != null) { node.headerText = CarName; return; }
            var obj = new GameObject("ScanNode");
            obj.transform.SetParent(transform);
            obj.transform.localPosition = Vector3.up * 0.5f;
            obj.layer = LayerMask.NameToLayer("ScanNode");
            node = obj.AddComponent<ScanNodeProperties>();
            node.maxRange = 40;
            node.minRange = 0;
            node.requiresLineOfSight = false;
            node.nodeType = 1;
            node.headerText = CarName;
            node.subText = "Retriever Unit";
            var sc = obj.AddComponent<SphereCollider>();
            sc.radius = 0.5f;
            sc.isTrigger = true;
        }

        IEnumerator InitRoutine()
        {
            LogIfDebugBuild("Retriever Unit waiting for NavMesh...");

            float waited = 0f;
            while (!agent.isOnNavMesh && waited < 10f)
            { yield return new WaitForSeconds(0.2f); waited += 0.2f; }

            if (!agent.isOnNavMesh)
            { agent.Warp(transform.position); yield return new WaitForSeconds(1f); }

            CacheAllEntrances();
            if (allOutsideEntrances.Count == 0)
            { yield return new WaitForSeconds(3f); CacheAllEntrances(); }

            FilterReachableEntrances();

            LogIfDebugBuild($"Reachable entrances: {reachableOutsideEntrances.Count} outside, {reachableInsideExits.Count} inside.");

            if (reachableOutsideEntrances.Count > 0)
                BeginGoToEntrance();
            else
            {
                // If there are no reachable entrances at all there is nothing we can do,
                // so we self-destruct rather than sitting idle forever.
                LogIfDebugBuild("No reachable entrances. Self-destructing.");
                StartCoroutine(ExplodeSequence());
            }
        }

        void CacheAllEntrances()
        {
            allOutsideEntrances.Clear();
            allInsideExits.Clear();
            foreach (var e in FindObjectsOfType<EntranceTeleport>())
            {
                if (e.isEntranceToBuilding) allOutsideEntrances.Add(e);
                else allInsideExits.Add(e);
            }
        }

        void FilterReachableEntrances()
        {
            reachableOutsideEntrances.Clear();
            foreach (var e in allOutsideEntrances)
                if (e != null && IsPositionReachable(e.transform.position))
                    reachableOutsideEntrances.Add(e);

            // We check inside exits only after teleporting in, so for now we keep them all.
            reachableInsideExits.Clear();
            reachableInsideExits.AddRange(allInsideExits);
        }

        void FilterReachableInsideExits()
        {
            reachableInsideExits.Clear();
            foreach (var e in allInsideExits)
                if (e != null && IsPositionReachable(e.transform.position))
                    reachableInsideExits.Add(e);

            // If nothing is reachable from inside, fall back to all exits so we don't get stuck.
            if (reachableInsideExits.Count == 0)
                reachableInsideExits.AddRange(allInsideExits);
        }

        bool IsPositionReachable(Vector3 pos)
        {
            if (!NavMesh.SamplePosition(pos, out NavMeshHit hit, 3f, NavMesh.AllAreas))
                return false;
            NavMeshPath path = new NavMeshPath();
            agent.CalculatePath(hit.position, path);
            return path.status == NavMeshPathStatus.PathComplete;
        }

        EntranceTeleport PickRandomReachableEntrance(EntranceTeleport exclude = null!)
        {
            if (reachableOutsideEntrances.Count == 0) return null!;
            var candidates = new List<EntranceTeleport>();
            foreach (var e in reachableOutsideEntrances)
                if (e != exclude) candidates.Add(e);

            // If there is no alternative we just use whatever is available.
            if (candidates.Count == 0) candidates = reachableOutsideEntrances;
            return candidates[CarRandom.Next(0, candidates.Count)];
        }

        EntranceTeleport PickRandomInsideExit() =>
            reachableInsideExits.Count == 0 ? null! :
            reachableInsideExits[CarRandom.Next(0, reachableInsideExits.Count)];

        public override void DoAIInterval()
        {
            moveTowardsDestination = true;
            movingTowardsTargetPlayer = false;
            base.DoAIInterval();

            if (isEnemyDead || isTeleporting || isParked || isExploding) return;

            switch ((State)currentBehaviourStateIndex)
            {
                case State.Init:
                    break;

                case State.GoingToEntrance:
                    agent.speed = activeSpeed;
                    if (chosenEntrance == null)
                    { chosenEntrance = PickRandomReachableEntrance(); if (chosenEntrance == null) break; }
                    SetDestinationToPosition(chosenEntrance.transform.position, checkForPath: false);
                    if (XZDist(transform.position, chosenEntrance.transform.position) < 2f)
                        StartCoroutine(TeleportInside());
                    break;

                case State.GoingToItem:
                    agent.speed = activeSpeed;
                    if (targetItem == null || targetItem.isHeld || targetItem.isPocketed)
                    { FindAndTargetItem(); break; }
                    SetDestinationToPosition(targetItem.transform.position, checkForPath: false);
                    CheckStuckOnItem();
                    // We use XZ distance here so tall items like the Apparatice are still picked up
                    // even when the car cant get to the exact same Y level as the item.
                    if (XZDist(transform.position, targetItem.transform.position) < 2.5f && !isPickingUp)
                    { stuckOnItemTimer = 0f; StartCoroutine(PickupRoutine()); }
                    break;

                case State.GoingToLockedDoor:
                    agent.speed = activeSpeed;
                    DoorLock targetDoor = FindNearestLockedDoor();
                    if (targetDoor == null)
                    {
                        LogIfDebugBuild("No locked doors found, going to exit.");
                        GoToExit();
                        break;
                    }
                    SetDestinationToPosition(targetDoor.transform.position, checkForPath: false);
                    if (XZDist(transform.position, targetDoor.transform.position) < 2f)
                    {
                        LogIfDebugBuild("At locked door with key. Unlocking.");
                        StartCoroutine(UnlockDoorWithKeyRoutine(targetDoor));
                    }
                    break;

                case State.PlacingLockpicker:
                    agent.speed = activeSpeed;
                    DoorLock blockerDoor = FindNearestLockedDoor();
                    if (blockerDoor == null) { GoToExit(); break; }
                    SetDestinationToPosition(blockerDoor.transform.position, checkForPath: false);
                    if (XZDist(transform.position, blockerDoor.transform.position) < 2f && !isPlacingLockpicker)
                        StartCoroutine(PlaceLockpickerRoutine(blockerDoor));
                    break;

                case State.GoingToExit:
                    agent.speed = activeSpeed;
                    EntranceTeleport exit = usedInsideExit ?? chosenInsideExit;
                    if (exit == null) { exit = PickRandomInsideExit(); chosenInsideExit = exit; }
                    if (exit == null) break;
                    SetDestinationToPosition(exit.transform.position, checkForPath: false);
                    if (XZDist(transform.position, exit.transform.position) < 2f)
                        StartCoroutine(TeleportOutside(exit));
                    break;

                case State.GoingToShip:
                    agent.speed = activeSpeed;
                    SetDestinationToPosition(cachedShipPos, checkForPath: false);
                    if (XZDist(transform.position, cachedShipPos) < 4f && !isDropping)
                        StartCoroutine(DropRoutine());
                    break;

                case State.Dropping:
                case State.Parked:
                    agent.speed = 0f;
                    break;
            }
        }

        void FindAndTargetItem()
        {
            // If we are already carrying something we shouldnt be looking for more items.
            if (carriedItem != null) { LogIfDebugBuild("Already carrying item, going to exit."); GoToExit(); return; }

            List<GrabbableObject> candidates = new List<GrabbableObject>();

            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item == null || item.isHeld || item.isInShipRoom || item.isPocketed) continue;
                if (item.gameObject == gameObject || item == targetItem) continue;
                if (!item.isInFactory) continue;

                Vector3 itemPos = item.transform.position;
                Vector3 navPos = itemPos;

                // We sample the NavMesh near the item rather than at the item itself,
                // because items like the Apparatice hang above the walkable surface.
                if (NavMesh.SamplePosition(itemPos, out NavMeshHit navHit, 4f, NavMesh.AllAreas))
                    navPos = navHit.position;

                float heightDiff = Mathf.Abs(itemPos.y - navPos.y);
                if (heightDiff > maxItemHeightDiff) continue;

                NavMeshPath path = new NavMeshPath();
                agent.CalculatePath(navPos, path);
                if (path.status != NavMeshPathStatus.PathComplete) continue;

                candidates.Add(item);
            }

            if (candidates.Count > 0)
            {
                // We pick a random item instead of the closest one so the cars can act like ants
                emptySearchCount = 0;
                GrabbableObject chosen = candidates[CarRandom.Next(0, candidates.Count)];
                LogIfDebugBuild($"Start targeting item: {chosen.itemProperties.itemName}");
                targetItem = chosen;
                stuckOnItemTimer = 0f;
                lastPosCheck = transform.position;
                lastPosCheckTime = Time.time;
                SwitchToBehaviourClientRpc((int)State.GoingToItem);

                Vector3 dest = targetItem.transform.position;
                if (NavMesh.SamplePosition(dest, out NavMeshHit h, 4f, NavMesh.AllAreas))
                    dest = h.position;
                SetDestinationToPosition(dest, checkForPath: false);
            }
            else
            {
                emptySearchCount++;
                LogIfDebugBuild($"No items found inside. Empty searches: {emptySearchCount}/{MAX_EMPTY_SEARCHES}");
                targetItem = null!;

                if (emptySearchCount >= MAX_EMPTY_SEARCHES)
                {
                    // We have confirmed there is nothing left to collect. LET FUCKING EXPLODE

                    LogIfDebugBuild("Confirmed no items. Self-destructing.");
                    StartCoroutine(ExplodeSequence());
                }
                else
                {
                    // Try going out and coming back through a different entrance next time.
                    LogIfDebugBuild("No items found, trying another entrance.");
                    GoToExit();
                }
            }
        }
        // im not sure about this one, needs to be fixed
        bool IsKey(GrabbableObject item)
        {
            if (item?.itemProperties == null) return false;
            return item.itemProperties.itemName.ToLower().Contains("key");
        }

        DoorLock FindNearestLockedDoor()
        {
            DoorLock nearest = null!;
            float bestDist = float.MaxValue;
            foreach (var door in FindObjectsOfType<DoorLock>())
            {
                if (door == null || !door.isLocked) continue;
                float d = Vector3.Distance(transform.position, door.transform.position);
                if (d < bestDist)
                {
                    NavMeshPath path = new NavMeshPath();
                    agent.CalculatePath(door.transform.position, path);
                    if (path.status != NavMeshPathStatus.PathComplete) continue;
                    bestDist = d;
                    nearest = door;
                }
            }
            return nearest!;
        }

        IEnumerator UnlockDoorWithKeyRoutine(DoorLock door)
        {
            agent.speed = 0f;
            yield return new WaitForSeconds(0.5f);

            if (door != null && door.isLocked)
            {
                LogIfDebugBuild("Unlocking door with key.");
                door.UnlockDoorSyncWithServer();

                if (carriedItem != null)
                {
                    carriedItem.transform.SetParent(null);
                    carriedItem.isHeld = false;
                    carriedItem.isPocketed = false;
                    if (carriedItem.TryGetComponent<NetworkObject>(out var netObj))
                        netObj.Despawn();
                    carriedItem = null!;
                    carriedItemIsKey = false;
                }
            }

            LogIfDebugBuild("Door unlocked. Searching for next item.");
            FindAndTargetItem();
        }

        DoorLock FindBlockingLockedDoor()
        {
            DoorLock best = null!;
            float bestDist = lockpickerPlaceRadius;
            foreach (var door in FindObjectsOfType<DoorLock>())
            {
                if (door == null || !door.isLocked) continue;
                float d = Vector3.Distance(transform.position, door.transform.position);
                if (d < bestDist)
                {
                    NavMeshPath path = new NavMeshPath();
                    agent.CalculatePath(door.transform.position, path);
                    if (path.status != NavMeshPathStatus.PathComplete) continue;
                    bestDist = d;
                    best = door;
                }
            }
            return best!;
        }

        GrabbableObject FindLockpickerItem()
        {
            if (heldLockpicker != null) return heldLockpicker;
            foreach (var item in FindObjectsOfType<GrabbableObject>())
            {
                if (item == null || item.isHeld || item.itemProperties == null) continue;
                if (item.itemProperties.itemName.ToLower().Contains("lockpicker"))
                    return item;
            }
            return null!;
        }

        IEnumerator PlaceLockpickerRoutine(DoorLock door)
        {
            isPlacingLockpicker = true;
            agent.speed = 0f;
            yield return new WaitForSeconds(0.5f);

            if (door == null || !door.isLocked)
            { isPlacingLockpicker = false; GoToExit(); yield break; }

            GrabbableObject lp = FindLockpickerItem();
            if (lp != null)
            {
                LogIfDebugBuild("Placing lockpicker on door.");
                lp.transform.SetParent(door.transform);
                lp.transform.localPosition = new Vector3(0f, 0f, -0.1f);
                lp.transform.localRotation = Quaternion.identity;
                lp.isHeld = false;
                lp.isPocketed = false;

                yield return new WaitForSeconds(5f);

                if (door != null && door.isLocked)
                    door.UnlockDoorSyncWithServer();

                // We pick it back up so we can reuse it on the next door we encounter.
                if (lp != null)
                {
                    lp.transform.SetParent(itemHoldPoint != null ? itemHoldPoint : transform);
                    lp.transform.localPosition = new Vector3(0.2f, 0f, 0f);
                    lp.isHeld = true;
                    lp.isPocketed = true;
                    heldLockpicker = lp;
                    LogIfDebugBuild("Lockpicker recovered.");
                }
            }
            else
            {
                LogIfDebugBuild("No lockpicker available, stuck timer will switch target.");
            }

            isPlacingLockpicker = false;
            SwitchToBehaviourClientRpc((int)State.GoingToItem);
            FindAndTargetItem();
        }

        void CheckStuckOnItem()
        {
            if (Time.time - lastPosCheckTime < 2f) return;

            float moved = Vector3.Distance(transform.position, lastPosCheck);
            if (moved < 0.5f)
            {
                stuckOnItemTimer += 2f;

                // If we havent moved its possible a locked door is in the way.
                DoorLock blocker = FindBlockingLockedDoor();
                if (blocker != null && stuckOnItemTimer >= 4f)
                {
                    LogIfDebugBuild("Locked door is blocking path. Switching to lockpicker.");
                    stuckOnItemTimer = 0f;
                    SwitchToBehaviourClientRpc((int)State.PlacingLockpicker);
                    return;
                }

                if (stuckOnItemTimer >= itemStuckTime)
                {
                    LogIfDebugBuild("Stuck too long, switching to a different item.");
                    stuckOnItemTimer = 0f;
                    targetItem = null!;
                    FindAndTargetItem();
                }
            }
            else
            {
                stuckOnItemTimer = 0f;
            }

            lastPosCheck = transform.position;
            lastPosCheckTime = Time.time;
        }

        IEnumerator ExplodeSequence()
        {
            if (isEnemyDead || isExploding) yield break;
            isExploding = true;
            LogIfDebugBuild("Retriever Unit self-destructing.");

            agent.speed = 0f;
            agent.isStopped = true;
            DropItemLocal();

            // Play the error sound a few times before exploding so the player has a warning.
            PlaySound(errorSFX);
            yield return new WaitForSeconds(0.3f);
            PlaySound(errorSFX);
            yield return new WaitForSeconds(0.3f);
            PlaySound(errorSFX);
            yield return new WaitForSeconds(0.5f);

            if (isEnemyDead) yield break;

            Landmine.SpawnExplosion(transform.position, true, 4f, 6f, 50, 80f, null, false);
            KillEnemyOnOwnerClient();
        }

        IEnumerator TeleportInside()
        {
            isTeleporting = true;
            agent.speed = 0f;
            usedOutsideEntrance = chosenEntrance;

            // exitPoint on the outside entrance leads to the inside spawn point.
            Transform dest = chosenEntrance?.exitPoint;
            if (dest == null && allInsideExits.Count > 0)
                dest = allInsideExits[0].transform;
            if (dest == null) { LogIfDebugBuild("No inside teleport point found!"); isTeleporting = false; yield break; }

            yield return new WaitForSeconds(0.3f);
            agent.enabled = false;
            transform.position = dest.position;
            agent.enabled = true;
            agent.Warp(dest.position);
            yield return new WaitForEndOfFrame();

            // We try to find the matching inside exit so we can leave through the same door we came in.
            usedInsideExit = dest.GetComponent<EntranceTeleport>()
                           ?? dest.GetComponentInParent<EntranceTeleport>();
            chosenInsideExit = usedInsideExit ?? PickRandomInsideExit();

            FilterReachableInsideExits();

            isPickingUp = false;
            isDropping = false;
            targetItem = null!;
            stuckOnItemTimer = 0f;
            isTeleporting = false;

            LogIfDebugBuild("Teleported inside. Searching for items.");
            SwitchToBehaviourClientRpc((int)State.GoingToItem);
            FindAndTargetItem();
        }

        IEnumerator TeleportOutside(EntranceTeleport insideExit)
        {
            isTeleporting = true;
            agent.speed = 0f;

            // exitPoint on the inside exit leads back to the outside spawn point.
            Transform dest = insideExit?.exitPoint;
            if (dest == null && usedOutsideEntrance != null) dest = usedOutsideEntrance.transform;
            if (dest == null && allOutsideEntrances.Count > 0) dest = allOutsideEntrances[0].transform;
            if (dest == null) { LogIfDebugBuild("No outside teleport point found!"); isTeleporting = false; yield break; }

            yield return new WaitForSeconds(0.3f);
            agent.enabled = false;
            transform.position = dest.position;
            agent.enabled = true;
            agent.Warp(dest.position);
            yield return new WaitForEndOfFrame();

            usedOutsideEntrance = null!;
            usedInsideExit = null!;
            isTeleporting = false;

            cachedShipPos = StartOfRound.Instance.elevatorTransform.position;
            if (NavMesh.SamplePosition(cachedShipPos, out NavMeshHit hit, 10f, NavMesh.AllAreas))
                cachedShipPos = hit.position;

            LogIfDebugBuild("Teleported outside. Returning to ship.");
            SwitchToBehaviourClientRpc((int)State.GoingToShip);
            SetDestinationToPosition(cachedShipPos, checkForPath: false);
        }

        IEnumerator PickupRoutine()
        {
            isPickingUp = true;
            agent.speed = 0f;
            yield return new WaitForSeconds(0.4f);

            if (targetItem == null || targetItem.isHeld || targetItem.isPocketed)
            {
                LogIfDebugBuild("Item was taken before we could pick it up.");
                isPickingUp = false;
                targetItem = null!;
                FindAndTargetItem();
                yield break;
            }

            GrabbableObject itemRef = targetItem;
            targetItem = null!;
            isPickingUp = false;

            var netObj = itemRef.GetComponent<NetworkObject>();
            if (netObj == null) { FindAndTargetItem(); yield break; }

            bool isKeyItem = IsKey(itemRef);
            LogIfDebugBuild($"Picked up item: {itemRef.itemProperties.itemName}");

            // We call a ClientRpc so every client sees the item attached to the Car.
            PickupItemClientRpc(netObj.NetworkObjectId);
            yield return new WaitForSeconds(0.2f);

            emptySearchCount = 0;
            carriedItemIsKey = isKeyItem;

            if (isKeyItem)
            {
                // If we picked up a key, go unlock the nearest locked door first
                // before leaving the building.
                DoorLock door = FindNearestLockedDoor();
                if (door != null)
                {
                    LogIfDebugBuild("Picked up a key! Going to locked door.");
                    SwitchToBehaviourClientRpc((int)State.GoingToLockedDoor);
                    SetDestinationToPosition(door.transform.position, checkForPath: false);
                    yield break;
                }
                else LogIfDebugBuild("Picked up a key but no locked doors found.");
            }

            GoToExit();
        }

        [ClientRpc]
        void PickupItemClientRpc(ulong itemNetId)
        {
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects
                .TryGetValue(itemNetId, out var netObj)) return;
            GrabbableObject item = netObj.GetComponent<GrabbableObject>();
            if (item == null || item.isHeld) return;

            PlaySound(pickupSFX);
            item.transform.SetParent(itemHoldPoint != null ? itemHoldPoint : transform);
            item.transform.localPosition = Vector3.zero;
            item.transform.localRotation = Quaternion.identity;
            item.isHeld = true;
            item.isPocketed = true;
            carriedItem = item;
        }

        void GoToExit()
        {
            EntranceTeleport exit = usedInsideExit ?? chosenInsideExit ?? PickRandomInsideExit();
            if (exit != null)
            {
                chosenInsideExit = exit;
                SwitchToBehaviourClientRpc((int)State.GoingToExit);
                SetDestinationToPosition(exit.transform.position, checkForPath: false);
            }
            else
            {
                // In case the exit is completely unreachable we drop the item and deal with it.
                LogIfDebugBuild("No exit found! Dropping item.");
                DropItemLocal();
            }
        }

        IEnumerator DropRoutine()
        {
            isDropping = true;
            SwitchToBehaviourClientRpc((int)State.Dropping);
            agent.speed = 0f;
            yield return new WaitForSeconds(0.8f);

            if (carriedItem != null)
            {
                var netObj = carriedItem.GetComponent<NetworkObject>();
                if (netObj != null) DropItemClientRpc(netObj.NetworkObjectId, true);
                carriedItem = null!;
            }

            yield return new WaitForSeconds(0.5f);
            isDropping = false;
            carriedItemIsKey = false;

            if (!agent.isOnNavMesh &&
                NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                agent.Warp(hit.position);

            // We exclude the entrance we just used so the Car tries a different one next cycle.
            // This is what fixes the Mineshaft layout where one entrance has no NavMesh. Crappy, but it works
            FilterReachableEntrances();
            BeginGoToEntrance(excludeEntrance: chosenEntrance);
        }

        void DropItemLocal()
        {
            if (carriedItem == null) return;
            var netObj = carriedItem.GetComponent<NetworkObject>();
            if (netObj != null) { DropItemClientRpc(netObj.NetworkObjectId, false); carriedItem = null!; }
        }

        [ClientRpc]
        void DropItemClientRpc(ulong itemNetId, bool dropOnShip)
        {
            if (!NetworkManager.Singleton.SpawnManager.SpawnedObjects
                .TryGetValue(itemNetId, out var netObj)) return;
            GrabbableObject item = netObj.GetComponent<GrabbableObject>();
            if (item == null) return;

            PlaySound(dropSFX);
            item.transform.SetParent(null);

            Vector3 dropPos;
            if (dropOnShip && StartOfRound.Instance.insideShipPositions?.Length > 0)
                dropPos = StartOfRound.Instance.insideShipPositions[
                    CarRandom.Next(0, StartOfRound.Instance.insideShipPositions.Length)].position;
            else
                dropPos = itemHoldPoint != null ? itemHoldPoint.position : transform.position;

            item.transform.position = dropPos;
            item.isHeld = false;
            item.isPocketed = false;
            item.startFallingPosition = dropPos;
            item.FallToGround();
            carriedItem = null!;
        }

        void BeginGoToEntrance(EntranceTeleport excludeEntrance = null!)
        {
            if (!agent.isOnNavMesh &&
                NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                agent.Warp(hit.position);

            chosenEntrance = PickRandomReachableEntrance(excludeEntrance);
            chosenInsideExit = null!;

            if (chosenEntrance == null)
            {
                LogIfDebugBuild("No reachable entrances left. Self-destructing.");
                StartCoroutine(ExplodeSequence());
                return;
            }

            LogIfDebugBuild($"Going to entrance: {chosenEntrance.name}");
            SwitchToBehaviourClientRpc((int)State.GoingToEntrance);
            SetDestinationToPosition(chosenEntrance.transform.position, checkForPath: false);
        }

        static float XZDist(Vector3 a, Vector3 b) =>
            new Vector2(a.x - b.x, a.z - b.z).magnitude;

        void PlaySound(AudioClip clip)
        {
            if (clip == null || creatureVoice == null) return;
            creatureVoice.PlayOneShot(clip);
        }

        public override void HitEnemy(int force = 1, PlayerControllerB? playerWhoHit = null,
            bool playHitSFX = false, int hitID = -1)
        {
            base.HitEnemy(force, playerWhoHit, playHitSFX, hitID);
            if (isEnemyDead) return;
            PlaySound(errorSFX);
            enemyHP -= force;
            if (IsOwner && enemyHP <= 0 && !isEnemyDead)
                StartCoroutine(ExplodeSequence());
        }

        public override void KillEnemy(bool destroy = false)
        {
            base.KillEnemy(destroy);
            DropItemLocal();
            UnregisterRadarTarget();
            if (CarAmbientAudio != null) CarAmbientAudio.Stop();
        }
    }
}