using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerAttackCombo : MonoBehaviour
{
    [System.Serializable]
    public class AttackStep
    {
        public string animationTrigger = "Attack1";
        public float duration = 0.5f;
        public bool moveForward = true;
        public float forwardSlide = 1.0f;
        public float comboQueueWindow = 0.2f;

        [Header("Hit Window (normalized 0..1)")]
        public float hitStart = 0.25f;  // when to begin checking for hits
        public float hitEnd = 0.45f;  // when to stop checking for hits
    }

    [Header("Combo")]
    public List<AttackStep> combo = new List<AttackStep>()
    {
        new AttackStep{ animationTrigger="Attack1", duration=0.45f, moveForward=true, forwardSlide=0.6f, comboQueueWindow=0.2f, hitStart=0.2f,  hitEnd=0.4f },
        new AttackStep{ animationTrigger="Attack2", duration=0.5f,  moveForward=true, forwardSlide=0.8f, comboQueueWindow=0.2f, hitStart=0.22f, hitEnd=0.45f },
        new AttackStep{ animationTrigger="Attack3", duration=0.6f,  moveForward=true, forwardSlide=1.0f, comboQueueWindow=0.25f, hitStart=0.25f, hitEnd=0.5f }
    };

    [Header("Detection")]
    public LayerMask enemyLayers;
    public float searchRadius = 10f;
    public float teleportIfWithin = 6f;
    public float frontOffset = 1.2f; // kept for compatibility (unused with pull-to)
    public float heightOffset = 0f;

    [Header("Hit Detection")]
    [Tooltip("Sphere center = hitPoint (if set), otherwise player forward * hitRange.")]
    public float hitRange = 1.8f;
    public float hitRadius = 1.1f;
    public Transform hitPoint;

    [Header("Hit Stop")]
    [Tooltip("Duration of the pause when a hit connects (realtime seconds). Fires once per step.")]
    public float hitStopDuration = 0.075f;
    [Range(0f, 1f)]
    [Tooltip("Timescale during hit stop (0 = full freeze).")]
    public float hitStopTimescale = 0f;

    [Header("Misc")]
    public Animator animator;
    public Transform modelRoot;
    public bool rotateToTarget = true;
    public float rotateSpeed = 720f;

    [Header("Input")]
    [Tooltip("Reference to your Gameplay Input Action Asset")]
    public InputActionAsset inputActions;

    [Header("Ultimate Attack")]
    [Tooltip("Ultimate animation trigger name")]
    public string ultimateAnimationTrigger = "Ultimate";
    [Tooltip("Duration of the ultimate animation")]
    public float ultimateDuration = 2.0f;
    [Tooltip("Explosion particle effect prefab")]
    public GameObject explosionEffect;
    [Tooltip("Distance in front of player to spawn explosion")]
    public float explosionDistance = 5.0f;

    [Header("Teleport")]
    [Tooltip("Extra gap to keep from touching colliders after the pull-to.")]
    public float standOffPadding = 0.25f;

    private InputAction attackAction;
    private InputAction ultimateAction;

    int comboIndex = 0;
    bool queuedNext = false;

    // Public accessor for other systems to check attack state
    public bool IsAttacking => isAttacking;
    public bool IsUltimateAttacking => isUltimateAttacking;
    private bool isAttacking = false;
    private bool isUltimateAttacking = false;

    // hit-stop bookkeeping
    Coroutine _hitStopCo;
    float _hitStopUntil = 0f;

    // per-step set so we don’t double-damage the same enemy in the same frame
    readonly HashSet<Transform> _hitThisStep = new HashSet<Transform>();

    private void Awake()
    {
        var gameplayMap = inputActions.FindActionMap("Gameplay");
        if (gameplayMap != null)
        {
            attackAction = gameplayMap.FindAction("Attack");
            ultimateAction = gameplayMap.FindAction("Ultimate");
        }
        else
            Debug.LogError("Gameplay action map not found in assigned InputActionAsset!");
    }

    private void OnEnable()
    {
        if (attackAction != null)
        {
            attackAction.Enable();
            attackAction.performed += OnAttack;
        }
        if (ultimateAction != null)
        {
            ultimateAction.Enable();
            ultimateAction.performed += OnUltimate;
        }
    }

    private void OnDisable()
    {
        if (attackAction != null)
        {
            attackAction.performed -= OnAttack;
            attackAction.Disable();
        }
        if (ultimateAction != null)
        {
            ultimateAction.performed -= OnUltimate;
            ultimateAction.Disable();
        }
    }

    private void OnAttack(InputAction.CallbackContext ctx)
    {
        TryStartOrQueueAttack();
    }

    private void OnUltimate(InputAction.CallbackContext ctx)
    {
        TryStartUltimate();
    }

    private void TryStartOrQueueAttack()
    {
        if (isAttacking || isUltimateAttacking)
        {
            queuedNext = true;
            return;
        }

        Transform target = FindNearestEnemy();
        if (target != null)
        {
            float dist = Vector3.Distance(transform.position, target.position);
            if (dist <= teleportIfWithin)
                BlinkToClosestAlongLine(target); // pull-to: stop just outside their collider along your current bearing

            if (rotateToTarget)
                FaceTargetImmediate(target.position);
        }

        StartCoroutine(DoComboRoutine(target));
    }

    private void TryStartUltimate()
    {
        if (isAttacking || isUltimateAttacking)
            return;

        Transform target = FindNearestEnemy();
        if (target != null && rotateToTarget)
            FaceTargetImmediate(target.position);

        StartCoroutine(DoUltimateRoutine());
    }

    private IEnumerator DoComboRoutine(Transform initialTarget)
    {
        isAttacking = true;
        comboIndex = 0;

        while (comboIndex < combo.Count)
        {
            AttackStep step = combo[comboIndex];

            if (animator && !string.IsNullOrEmpty(step.animationTrigger))
                animator.SetTrigger(step.animationTrigger);

            float elapsed = 0f;
            queuedNext = false;
            _hitThisStep.Clear();      // new step: no enemies recorded
            bool hitLagFiredThisStep = false; // <-- key flag: allow ONE hit-lag per step

            // optional forward slide at the start of the step
            if (step.moveForward && step.forwardSlide > 0f)
            {
                Vector3 start = transform.position;
                Vector3 end = start + (modelRoot ? modelRoot.forward : transform.forward) * step.forwardSlide;
                float slideTime = Mathf.Min(0.1f, step.duration * 0.25f);

                float t = 0f;
                while (t < slideTime)
                {
                    t += Time.deltaTime;
                    transform.position = Vector3.Lerp(start, end, t / slideTime);
                    yield return null;
                }
            }

            float queueOpenAt = Mathf.Max(0f, step.duration - step.comboQueueWindow);
            Transform target = initialTarget ? initialTarget : FindNearestEnemy();

            while (elapsed < step.duration)
            {
                elapsed += Time.deltaTime;

                // rotate toward (current) target if any
                if (rotateToTarget && target)
                {
                    Vector3 dir = target.position - transform.position; dir.y = 0f;
                    if (dir.sqrMagnitude > 0.0001f)
                    {
                        Quaternion to = Quaternion.LookRotation(dir.normalized, Vector3.up);
                        (modelRoot ? modelRoot : transform).rotation =
                            Quaternion.RotateTowards((modelRoot ? modelRoot : transform).rotation, to, rotateSpeed * Time.deltaTime);
                    }
                }

                // --- HIT WINDOW ---
                float n = Mathf.Clamp01(elapsed / step.duration);
                if (n >= step.hitStart && n <= step.hitEnd)
                {
                    int newHits = TryHitMultiple(_hitThisStep, out List<Transform> justHitList);
                    if (newHits > 0)
                    {
                        // Optional damage hook per enemy (only for those newly detected this frame)
                        foreach (var hitEnemy in justHitList)
                        {
                            var dmg = hitEnemy ? hitEnemy.GetComponentInParent<IDamageable>() : null;
                            if (dmg != null)
                            {
                                Vector3 hp = hitEnemy.position;
                                Vector3 hn = (hitEnemy.position - transform.position).normalized;
                                dmg.TakeHit(hp, hn, 1f);
                            }
                        }

                        // Fire hit-lag ONCE per step (first time we connect this step)
                        if (!hitLagFiredThisStep)
                        {
                            TriggerHitStop(hitStopDuration, hitStopTimescale);
                            hitLagFiredThisStep = true;
                        }
                    }
                }
                // -------------------

                yield return null;
            }

            if (queuedNext && comboIndex < combo.Count - 1)
                comboIndex++;
            else
                break;
        }

        isAttacking = false;
        queuedNext = false;
        comboIndex = 0;
    }

    private IEnumerator DoUltimateRoutine()
    {
        isUltimateAttacking = true;

        // Trigger ultimate animation
        if (animator && !string.IsNullOrEmpty(ultimateAnimationTrigger))
            animator.SetTrigger(ultimateAnimationTrigger);

        // Spawn explosion immediately in front of player
        //TriggerExplosionInFront();

        float elapsed = 0f;
        while (elapsed < ultimateDuration)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        isUltimateAttacking = false;
    }

    public void TriggerExplosionInFront()
    {
        if (explosionEffect == null) return;

        // Calculate position in front of player
        Vector3 playerForward = (modelRoot ? modelRoot.forward : transform.forward);
        Vector3 spawnPosition = transform.position + playerForward * explosionDistance;

        GameObject explosion = Instantiate(explosionEffect, spawnPosition, Quaternion.identity);
        
        // Optional: Auto-destroy the explosion effect after some time
        ParticleSystem particles = explosion.GetComponent<ParticleSystem>();
        if (particles != null)
        {
            float destroyTime = particles.main.duration + particles.main.startLifetime.constantMax;
            Destroy(explosion, destroyTime);
        }
        else
        {
            // Fallback: destroy after 5 seconds if no particle system found
            Destroy(explosion, 5f);
        }
    }

    private Transform FindNearestEnemy()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, searchRadius, enemyLayers);
        Transform best = null;
        float bestDist = float.MaxValue;

        foreach (var h in hits)
        {
            Transform t = h.attachedRigidbody ? h.attachedRigidbody.transform : h.transform;
            float d = (t.position - transform.position).sqrMagnitude;
            if (d < bestDist)
            {
                bestDist = d;
                best = t;
            }
        }

        return best;
    }

    // Pull-to: stop just outside combined radii along the current bearing (keep same side)
    void BlinkToClosestAlongLine(Transform enemy)
    {
        Vector3 enemyToPlayer = transform.position - enemy.position;
        enemyToPlayer.y = 0f;
        if (enemyToPlayer.sqrMagnitude < 0.0001f) return;

        Vector3 dir = enemyToPlayer.normalized;

        float enemyR = EstimateHorizontalRadius(enemy);
        float playerR = EstimateHorizontalRadius(transform);
        float stopDist = Mathf.Max(0.05f, enemyR + playerR + standOffPadding);

        Vector3 target = enemy.position + dir * stopDist;
        Vector3 newPos = new Vector3(target.x, transform.position.y + heightOffset, target.z);
        transform.position = newPos;

        FaceTargetImmediate(enemy.position);
    }

    float EstimateHorizontalRadius(Transform t)
    {
        var cc = t.GetComponent<CharacterController>();
        if (cc) return cc.radius;

        var cap = t.GetComponent<CapsuleCollider>();
        if (cap) return cap.radius;

        var sph = t.GetComponent<SphereCollider>();
        if (sph) return sph.radius;

        var col = t.GetComponent<Collider>();
        if (col)
        {
            var ext = col.bounds.extents;
            return Mathf.Max(ext.x, ext.z);
        }
        return 0.5f;
    }

    // --- Hit detection & hit stop helpers ---

    // Returns count of NEW enemies hit this frame (not yet seen this frame).
    // Adds them to 'alreadyHitThisStep' and returns list via 'justHit'.
    int TryHitMultiple(HashSet<Transform> alreadyHitThisStep, out List<Transform> justHit)
    {
        justHit = null;

        Vector3 center;
        if (hitPoint != null)
        {
            center = hitPoint.position;
        }
        else
        {
            // safe fallback if hitPoint is not assigned: use player forward offset
            Vector3 fwd = (modelRoot ? modelRoot.forward : transform.forward);
            center = transform.position + fwd * hitRange;
        }

        Collider[] hits = Physics.OverlapSphere(center, hitRadius, enemyLayers, QueryTriggerInteraction.Ignore);
        if (hits == null || hits.Length == 0) return 0;

        int newCount = 0;
        foreach (var h in hits)
        {
            Transform t = h.attachedRigidbody ? h.attachedRigidbody.transform : h.transform;
            if (t == null) continue;

            // Only count each enemy once per frame here
            if (!alreadyHitThisStep.Contains(t))
            {
                alreadyHitThisStep.Add(t);
                (justHit ??= new List<Transform>()).Add(t);
                newCount++;
            }
        }
        return newCount;
    }

    void TriggerHitStop(float duration, float timescale)
    {
        float now = Time.realtimeSinceStartup;
        _hitStopUntil = Mathf.Max(_hitStopUntil, now + duration);
        if (_hitStopCo == null)
            _hitStopCo = StartCoroutine(HitStopRoutine(timescale));
    }

    IEnumerator HitStopRoutine(float timescale)
    {
        float prevScale = Time.timeScale;
        Time.timeScale = timescale;
        while (Time.realtimeSinceStartup < _hitStopUntil)
            yield return null;
        Time.timeScale = prevScale;
        _hitStopCo = null;
    }

    private void FaceTargetImmediate(Vector3 worldPoint)
    {
        Vector3 dir = (worldPoint - transform.position);
        dir.y = 0;
        if (dir.sqrMagnitude > 0.001f)
        {
            Quaternion rot = Quaternion.LookRotation(dir.normalized);
            (modelRoot ? modelRoot : transform).rotation = rot;
        }
    }

    private void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.yellow;
        Gizmos.DrawWireSphere(transform.position, searchRadius);
        Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, teleportIfWithin);

        // visualize hit sphere
        Gizmos.color = new Color(1f, 0.3f, 0.3f, 0.5f);
        Vector3 center;
        if (hitPoint != null)
            center = hitPoint.position;
        else
        {
            Vector3 fwd = (modelRoot ? modelRoot.forward : transform.forward);
            center = transform.position + fwd * hitRange;
        }
        Gizmos.DrawWireSphere(center, hitRadius);

        // visualize explosion spawn position in front of player
        Gizmos.color = Color.red;
        Vector3 explosionPos = transform.position + (modelRoot ? modelRoot.forward : transform.forward) * explosionDistance;
        Gizmos.DrawWireSphere(explosionPos, 0.5f);
        Gizmos.DrawLine(transform.position, explosionPos);
    }
}

// Optional damage interface you can implement on enemies
public interface IDamageable
{
    void TakeHit(Vector3 hitPoint, Vector3 hitNormal, float damage);
}
