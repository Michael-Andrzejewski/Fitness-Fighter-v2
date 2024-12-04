using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
using TMPro;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

public class EvolCharBehavior : MonoBehaviour
{
    public GameObject closestEnemy;

    public EvolStats evolStats;
    public Animator animator;
    public NavMeshAgent agent;

    public GameObject clonableDuplicate;
    public SkinnedMeshRenderer skinnedMeshRenderer;

    public float attackRange;
    public bool isAttacking = false;
    public bool isMoving = false;

    public string tagToAttack = "Enemy";
    public TextMeshPro nameTag;
    public float generalEvolutionStdDeviation = 0.1f;
    public bool geneticInstability = false;
    public bool similarColor = false;

    // OpenAI Configuration
    private readonly string openAIEndpoint = "https://api.openai.com/v1/chat/completions";
    [SerializeField, Tooltip("Your OpenAI API Key")]
    private string apiKey;
    [SerializeField, Range(0.5f, 10f)]
    private float aiThinkingInterval = 2f;
    
    public Queue<string> messageHistory = new Queue<string>();
    private const int MAX_MESSAGE_HISTORY = 10;
    public TextMeshPro messageDisplay;
    [SerializeField]
    private float messageDisplayDuration = 5f;
    
    [System.Serializable]
    private class OpenAIRequest
    {
        public string model = "gpt-4o-mini";
        public List<MessageData> messages;
        public float temperature = 0.7f;
    }

    [System.Serializable]
    private class MessageData
    {
        public string role;
        public string content;
    }

    [System.Serializable]
    private class OpenAIResponse
    {
        public Choice[] choices;
    }

    [System.Serializable]
    private class Choice
    {
        public MessageData message;
    }

    void Start()
    {
        evolStats = GetComponent<EvolStats>();
        animator = GetComponent<Animator>();
        agent = GetComponent<NavMeshAgent>();

        agent.speed = evolStats.movementSpeed;
        attackRange = evolStats.attackRange;

        // Initialize message display
        if (messageDisplay == null)
        {
            GameObject messageObj = new GameObject("MessageDisplay");
            messageObj.transform.SetParent(transform);
            messageObj.transform.localPosition = Vector3.up * 3f; // Position above character
            messageDisplay = messageObj.AddComponent<TextMeshPro>();
            messageDisplay.alignment = TextAlignmentOptions.Center;
            messageDisplay.fontSize = 4;
        }

        StartCoroutine(AIDecisionLoop());
    }

    IEnumerator AIDecisionLoop()
    {
        while (true)
        {
            if (evolStats.currentHealth > 0)
            {
                yield return new WaitForSeconds(aiThinkingInterval);
                _ = MakeAIDecision();
            }
            yield return null;
        }
    }

    async Task MakeAIDecision()
    {
        string prompt = GeneratePrompt();
        string action = await GetAIResponse(prompt);
        Debug.Log($"<{gameObject.name}> AI Output: {action}");
        ProcessAIAction(action);
    }

    string GeneratePrompt()
    {
        StringBuilder prompt = new StringBuilder();
        prompt.AppendLine($"You are {gameObject.name}, a character in a fighting arena. Your stats are:");
        prompt.AppendLine($"Health: {evolStats.currentHealth}/{evolStats.maxHealth}");
        prompt.AppendLine($"Attack Damage: {evolStats.attackDamage}");
        prompt.AppendLine($"Movement Speed: {evolStats.movementSpeed}");
        
        if (closestEnemy != null)
        {
            var enemyStats = closestEnemy.GetComponent<EvolStats>();
            float distance = Vector3.Distance(transform.position, closestEnemy.transform.position);
            prompt.AppendLine($"\nNearest enemy is {closestEnemy.name} at distance {distance:F1}:");
            prompt.AppendLine($"Enemy Health: {enemyStats.currentHealth}/{enemyStats.maxHealth}");
        }

        prompt.AppendLine("\nRecent messages:");
        foreach (string msg in messageHistory)
        {
            prompt.AppendLine(msg);
        }

        prompt.AppendLine("\nRespond with ONE action in brackets:");
        prompt.AppendLine("[attack \"enemyName\"] - to attack a specific enemy");
        prompt.AppendLine("[message \"enemyName\": Your message] - to communicate");
        prompt.AppendLine("[move] - to move towards the nearest enemy");

        return prompt.ToString();
    }

    async Task<string> GetAIResponse(string prompt)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var request = new OpenAIRequest
            {
                messages = new List<MessageData>
                {
                    new MessageData { role = "system", content = "You are a fighting character. Respond with exactly ONE action in brackets." },
                    new MessageData { role = "user", content = prompt }
                }
            };

            var jsonRequest = JsonUtility.ToJson(request);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(openAIEndpoint, content);
            var responseString = await response.Content.ReadAsStringAsync();
            var openAIResponse = JsonUtility.FromJson<OpenAIResponse>(responseString);

            return openAIResponse.choices[0].message.content;
        }
    }

    void ProcessAIAction(string action)
    {
        // Parse attack command
        var attackMatch = Regex.Match(action, @"\[attack ""([^""]+)""\]");
        if (attackMatch.Success)
        {
            string targetName = attackMatch.Groups[1].Value;
            var target = GameObject.Find(targetName);
            if (target != null)
            {
                closestEnemy = target;
                if (Vector3.Distance(transform.position, target.transform.position) < attackRange)
                {
                    Debug.Log($"<{gameObject.name}> Attacking {targetName}!");
                    StartCoroutine(Attack());
                }
            }
            return;
        }

        // Parse message command
        var messageMatch = Regex.Match(action, @"\[message ""([^""]+)"": ([^\]]+)\]");
        if (messageMatch.Success)
        {
            string targetName = messageMatch.Groups[1].Value;
            string message = messageMatch.Groups[2].Value;
            DisplayMessage(message);
            string formattedMessage = $"<{gameObject.name}> to {targetName}: {message}";
            Debug.Log(formattedMessage);
            AddToMessageHistory(formattedMessage);
            return;
        }

        // Parse move command
        if (action.Contains("[move]"))
        {
            Debug.Log($"<{gameObject.name}> Moving towards enemy.");
            MoveTowardsEnemy();
        }
    }

    void DisplayMessage(string message)
    {
        if (messageDisplay != null)
        {
            messageDisplay.text = $"{gameObject.name}: {message}";
            StartCoroutine(ClearMessageAfterDelay());
        }
    }

    IEnumerator ClearMessageAfterDelay()
    {
        yield return new WaitForSeconds(messageDisplayDuration);
        if (messageDisplay != null)
        {
            messageDisplay.text = "";
        }
    }

    void AddToMessageHistory(string message)
    {
        messageHistory.Enqueue(message);
        if (messageHistory.Count > MAX_MESSAGE_HISTORY)
        {
            messageHistory.Dequeue();
        }
    }

    // Modify the Update method to remove automatic combat
    void Update()
    {
        if (evolStats.currentHealth <= 0)
        {
            return;
        }

        // Only update movement if we're not attacking
        if (!isAttacking && agent.enabled)
        {
            agent.speed = evolStats.movementSpeed;
        }
    }

    void MoveTowardsEnemy()
    {
        closestEnemy = FindClosestEnemy();
        if (closestEnemy == null || closestEnemy.GetComponent<EvolStats>().currentHealth <= 0 || evolStats.currentHealth <= 0)
        {
            return;
        }
        if (GetComponent<NavMeshAgent>().enabled && closestEnemy.GetComponent<NavMeshAgent>().enabled)
        {
            agent.SetDestination(closestEnemy.transform.position);
        }
    }

    GameObject FindClosestEnemy()
    {
        GameObject[] enemies;
        enemies = GameObject.FindGameObjectsWithTag(tagToAttack);
        GameObject closest = null;
        float distance = Mathf.Infinity;
        Vector3 position = transform.position;
        foreach (GameObject enemy in enemies)
        {
            EvolStats enemyEvolStats = enemy.GetComponent<EvolStats>();
            if (enemy != gameObject && enemyEvolStats.currentHealth > 0)
            {
                // Check if the faction of the enemy is not the same as this character's faction
                string enemyFaction = enemyEvolStats.faction;
                string thisFaction = evolStats.faction;

                if (thisFaction != enemyFaction || string.IsNullOrEmpty(thisFaction) || string.IsNullOrEmpty(enemyFaction))
                {
                    Vector3 diff = enemy.transform.position - position;
                    float curDistance = diff.sqrMagnitude;
                    if (curDistance < distance)
                    {
                        closest = enemy;
                        distance = curDistance;
                    }
                }
            }
        }
        return closest;
    }

    IEnumerator Attack()
    {
        if (closestEnemy == null || evolStats.currentHealth <= 0 ||
            closestEnemy.GetComponent<EvolStats>().currentHealth <= 0)
        {
            isAttacking = false;
            yield break;
        }

        agent.SetDestination(transform.position);
        transform.LookAt(closestEnemy.transform);

        animator.SetBool("isPunching", true);
        animator.SetFloat("punchingSpeed", (1.2f / (evolStats.attackSpeed / 2)));

        yield return new WaitForSeconds(evolStats.attackSpeed / 2);

        EvolStats enemyEvolStats = closestEnemy.GetComponent<EvolStats>();

        if (Vector3.Distance(transform.position, closestEnemy.transform.position) < attackRange)
        {
            if (!enemyEvolStats.killed)
            {
                enemyEvolStats.currentHealth -= evolStats.attackDamage;
                Debug.Log($"<{gameObject.name}> Hit {closestEnemy.name} for {evolStats.attackDamage} damage! {closestEnemy.name}'s health: {enemyEvolStats.currentHealth}/{enemyEvolStats.maxHealth}");
            }

            if (enemyEvolStats.currentHealth <= 0 && !enemyEvolStats.killed)
            {
                enemyEvolStats.killed = true;
                Debug.Log($"<{gameObject.name}> Defeated {closestEnemy.name}!");

                closestEnemy.GetComponent<Animator>().SetBool("Death", true);
                closestEnemy.GetComponent<NavMeshAgent>().enabled = false;
                closestEnemy.AddComponent<DeleteAfterSeconds>().seconds = 30;

                closestEnemy.GetComponent<EvolCharBehavior>().enabled = false;
                closestEnemy.tag = "Dead";

                SpawnClone();
            }
        }

        animator.SetBool("isPunching", false);

        yield return new WaitForSeconds(evolStats.attackSpeed / 2);

        if (closestEnemy != null && Vector3.Distance(transform.position, closestEnemy.transform.position) < attackRange &&
            closestEnemy.GetComponent<EvolStats>().currentHealth > 0 && evolStats.currentHealth > 0)
        {
            StartCoroutine(Attack());
        }
        else
        {
            isAttacking = false;
        }
    }

    void SpawnClone()
    {
        // Spawn somewhere within 50 units of the parent
        Vector3 randomPosition = transform.position + Random.insideUnitSphere * 50;
        NavMesh.SamplePosition(randomPosition, out NavMeshHit hit, 50, 1);

        // Instantiate the clonableDuplicate instead of the gameObject itself
        GameObject clone = Instantiate(clonableDuplicate, hit.position, Quaternion.identity);

        // Apply the parent's color to the clone
        Color parentColor = skinnedMeshRenderer.material.color;

        // Find "beta_surface" SkinnedMeshRenderer in the clone's children
        SkinnedMeshRenderer cloneRenderer = null;
        SkinnedMeshRenderer[] renderers = clone.GetComponentsInChildren<SkinnedMeshRenderer>();
        foreach (SkinnedMeshRenderer renderer in renderers)
        {
            if (renderer.name == "Beta_Surface")
            {
                cloneRenderer = renderer;
                break;
            }
        }

        if (cloneRenderer != null)
        {
            if (similarColor)
            {
                parentColor.r += Random.Range(-0.1f, 0.1f);
                parentColor.g += Random.Range(-0.1f, 0.1f);
                parentColor.b += Random.Range(-0.1f, 0.1f);
                parentColor.r = Mathf.Clamp01(parentColor.r);
                parentColor.g = Mathf.Clamp01(parentColor.g);
                parentColor.b = Mathf.Clamp01(parentColor.b);

            }

            cloneRenderer.material.color = parentColor;
        }

        EvolStats cloneStats = clone.GetComponent<EvolStats>();

        // Apply the parent's Clonable Duplicate to the clone
        EvolCharBehavior cloneBehavior = clone.GetComponent<EvolCharBehavior>();
        cloneBehavior.clonableDuplicate = clonableDuplicate;

        clone.tag = "Enemy";

        UpdateCloneName(clone);

        // Remove the "Delete After Seconds" component if it exists on the spawned child
        if (clone.GetComponent<DeleteAfterSeconds>() != null)
        {
            Destroy(clone.GetComponent<DeleteAfterSeconds>());
        }

        // Apply Gaussian distribution to stats
        cloneStats.attackDamage = evolStats.attackDamage * (1 + RandomGaussian(0, generalEvolutionStdDeviation));
        if (cloneStats.attackDamage <= 0)
        {
            cloneStats.attackDamage = 1f;
        }
        cloneStats.maxHealth = evolStats.maxHealth * (1 + RandomGaussian(0, generalEvolutionStdDeviation));
        if (cloneStats.maxHealth <= 0)
        {
            cloneStats.maxHealth = 1f;
        }
        cloneStats.currentHealth = cloneStats.maxHealth;
        cloneStats.movementSpeed = evolStats.movementSpeed + RandomGaussian(0, generalEvolutionStdDeviation);

        if (geneticInstability)
        {
            cloneBehavior.generalEvolutionStdDeviation = generalEvolutionStdDeviation + RandomGaussian(0, generalEvolutionStdDeviation);
            //do not let it go negative
            if (cloneBehavior.generalEvolutionStdDeviation <= 0)
            {
                cloneBehavior.generalEvolutionStdDeviation = generalEvolutionStdDeviation;
            }
            cloneBehavior.geneticInstability = geneticInstability;
        }

        clone.GetComponent<NavMeshAgent>().speed = cloneStats.movementSpeed;
        cloneBehavior.similarColor = similarColor;
        cloneStats.charName = evolStats.charName;
    }

    float RandomGaussian(float mean, float stdDev)
    {
        float u1 = Random.value;
        float u2 = Random.value;
        float randStdNormal = Mathf.Sqrt(-2.0f * Mathf.Log(u1)) * Mathf.Sin(2.0f * Mathf.PI * u2);
        return mean + stdDev * randStdNormal;
    }

    void UpdateCloneName(GameObject clone)
    {
        string parentName = gameObject.name;
        int genIndex = parentName.IndexOf("Gen");
        if (genIndex != -1)
        {
            int generationNumber = 0;
            if (int.TryParse(parentName.Substring(genIndex + 4), out generationNumber))
            {
                generationNumber++;
                string updatedName = parentName.Substring(0, genIndex) + "Gen " + generationNumber;
                clone.name = updatedName;
            }
            else
            {
                // Default behavior in case parsing fails
                clone.name = parentName + " - Gen 2";
            }
        }
        else
        {
            // If the parent doesn't have a generation number, set it to Gen 2 for the clone
            clone.name = parentName + " - Gen 2";
        }

        // Update the name tag of the clone
        TextMeshPro cloneNameTag = clone.GetComponentInChildren<TextMeshPro>();
        if (cloneNameTag != null)
        {
            cloneNameTag.text = clone.name;
        }
    }
}