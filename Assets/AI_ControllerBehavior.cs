using UnityEngine;
using System.Collections.Generic;
using UnityEngine.AI;
using System.Collections;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

public class AI_ControllerBehavior : MonoBehaviour
{
    [Header("AI State")]
    public bool isAttacking = false;
    public string targetName;

    [Header("AI Prompts")]
    [TextArea(3, 10)]
    public string systemPrompt = "Default personality and strategy";
    [TextArea(3, 10)]
    public string instructionPrompt;
    [TextArea(3, 10)]
    public string contextPrompt;

    [Header("AI Behavior")]
    public float actionCooldown = 10f;
    private float nextActionTime;

    [Header("Reproduction")]
    public GameObject aiAgentPrefab;
    private static HashSet<string> usedNames = new HashSet<string>();

    // Reference to nearby entities
    private List<AI_ControllerBehavior> nearbyAgents = new List<AI_ControllerBehavior>();
    private List<AI_ControllerBehavior> allies = new List<AI_ControllerBehavior>();
    private List<AI_ControllerBehavior> enemies = new List<AI_ControllerBehavior>();

    [Header("Combat")]
    public float attackRange = 2f;
    public float attackSpeed = 1f;
    public float attackDamage = 1f;
    private NavMeshAgent agent;
    private Animator animator;

    [Header("Communication")]
    [TextArea(10,30)]
    public string globalChatHistory = "";
    private const int MAX_CHAT_LENGTH = 1000;
    [TextArea(2,5)]
    public string messageToSend = "";
    public string receiverName = "";
    [SerializeField]
    private bool sendMessageButton;  // This will show up as a checkbox in the inspector

    [Header("AI Control")]
    public bool aiActive = false;

    // OpenAI Configuration
    private readonly string openAIEndpoint = "https://api.openai.com/v1/chat/completions";
    [SerializeField, Tooltip("Your OpenAI API Key")]
    private string apiKey;
    [SerializeField, Range(0.5f, 20f)]
    private float aiThinkingInterval = 2f;

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
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        
        if (aiActive)
        {
            StartCoroutine(AIDecisionLoop());
        }
    }

    private void Update()
    {
        // Check for death first
        var stats = GetComponent<EvolStats>();
        if (stats != null && stats.currentHealth <= 0 && !stats.killed)
        {
            // Mark as killed to prevent multiple executions
            stats.killed = true;
            
            // Play death animation
            if (animator != null)
            {
                animator.SetBool("Death", true);
            }
            
            // Disable navigation and AI control
            if (agent != null)
            {
                agent.enabled = false;
            }
            
            // Change tag and disable AI
            gameObject.tag = "Dead";
            enabled = false;
            
            // Optional: Add deletion after delay
            gameObject.AddComponent<DeleteAfterSeconds>().seconds = 30;
            
            return;
        }

        if (Time.time >= nextActionTime)
        {
            UpdateContextPrompt();
            DecideAction();
            nextActionTime = Time.time + actionCooldown;
        }

        // Handle movement and attacking
        if (!string.IsNullOrEmpty(targetName) && !isAttacking)
        {
            GameObject target = GameObject.Find(targetName);
            if (target != null && target != gameObject)
            {
                float distance = Vector3.Distance(transform.position, target.transform.position);
                if (distance <= attackRange)
                {
                    StartCoroutine(Attack(target));
                }
                else if (agent != null && agent.enabled)
                {
                    agent.SetDestination(target.transform.position);
                }
            }
        }
    }

    IEnumerator Attack(GameObject target)
    {
        if (isAttacking || target == null || target == gameObject)
            yield break;

        isAttacking = true;

        while (target != null)
        {
            // Check target's health and tag
            var targetStats = target.GetComponent<EvolStats>();
            if (targetStats == null || targetStats.currentHealth <= 0 || targetStats.killed || target.CompareTag("Dead"))
            {
                break;  // Exit the attack loop if target is dead
            }

            float distance = Vector3.Distance(transform.position, target.transform.position);
            
            if (distance > attackRange)
            {
                if (agent != null && agent.enabled)
                {
                    agent.SetDestination(target.transform.position);
                }
                yield return null;
                continue;
            }

            // Stop moving and face target
            if (agent != null)
                agent.SetDestination(transform.position);
            transform.LookAt(target.transform);

            // Trigger attack animation if available
            if (animator != null)
            {
                animator.SetBool("isPunching", true);
                animator.SetFloat("punchingSpeed", 1f / attackSpeed);
            }

            // Wait for attack animation midpoint
            yield return new WaitForSeconds(attackSpeed * 0.5f);

            // Apply damage
            if (!targetStats.killed && !target.CompareTag("Dead"))
            {
                targetStats.currentHealth -= attackDamage;
                Debug.Log($"<{gameObject.name}> Hit {target.name} for {attackDamage} damage! {target.name}'s health: {targetStats.currentHealth}/{targetStats.maxHealth}");

                if (targetStats.currentHealth <= 0)
                {
                    OnDefeatEnemy();
                    break;
                }
            }

            // Complete attack animation
            yield return new WaitForSeconds(attackSpeed * 0.5f);
            if (animator != null)
            {
                animator.SetBool("isPunching", false);
            }
        }

        // Make sure animation is stopped when breaking out of the loop
        if (animator != null)
        {
            animator.SetBool("isPunching", false);
        }

        isAttacking = false;
        targetName = "";
    }

    private void UpdateContextPrompt()
    {
        // Build context about nearby entities, attackers, and current target
        contextPrompt = $"Nearby Agents:\n{GetNearbyAgentsInfo()}\n" +
                       $"Attackers:\n{GetAttackersInfo()}\n" +
                       $"Current Target: {targetName}\n" +
                       $"Allies:\n{GetAlliesInfo()}\n" +
                       $"Enemies:\n{GetEnemiesInfo()}";
    }

    private void DecideAction()
    {
        // Use LLM to decide whether to:
        // 1. Message another character
        // 2. Attack another character
        // Based on system, instruction, and context prompts
        
        // If decision is to send message, call:
        // SendMessage("Target Name", "Message content");
    }

    public void SendMessage(string targetName, string message)
    {
        string timestamp = System.DateTime.Now.ToString("HH:mm:ss");
        string formattedMessage = $"[{timestamp}] {gameObject.name} -> {targetName}: {message}\n";
        
        // Add to global chat history
        globalChatHistory += formattedMessage;
        
        // Check if we need to summarize
        if (globalChatHistory.Length > MAX_CHAT_LENGTH)
        {
            // TODO: Use LLM to generate summary
            // For now, just indicate a summary happened
            globalChatHistory = "[SUMMARY OF PREVIOUS CONVERSATIONS]\n" + formattedMessage;
        }
        
        // Find target and add to their history too
        GameObject target = GameObject.Find(targetName);
        if (target != null)
        {
            var targetAI = target.GetComponent<AI_ControllerBehavior>();
            if (targetAI != null)
            {
                targetAI.ReceiveMessage(formattedMessage);
            }
        }
    }

    public void ReceiveMessage(string formattedMessage)
    {
        globalChatHistory += formattedMessage;
        
        // Check if we need to summarize
        if (globalChatHistory.Length > MAX_CHAT_LENGTH)
        {
            // TODO: Use LLM to generate summary
            globalChatHistory = "[SUMMARY OF PREVIOUS CONVERSATIONS]\n" + formattedMessage;
        }
    }

    public void OnDefeatEnemy()
    {
        // Create child AI
        GameObject childAgent = Instantiate(aiAgentPrefab, transform.position, Quaternion.identity);
        AI_ControllerBehavior childAI = childAgent.GetComponent<AI_ControllerBehavior>();
        
        // Generate unique name
        string childName = GenerateUniqueName();
        usedNames.Add(childName);
        
        // Modify child's system prompt (personality/strategy)
        // This could be done through LLM interaction
        childAI.systemPrompt = GenerateChildSystemPrompt();
    }

    private string GenerateUniqueName()
    {
        // Logic to generate unique name
        // Could use LLM or predetermined list
        return "UniqueNameHere";
    }

    private string GenerateChildSystemPrompt()
    {
        // Logic to generate new system prompt for child
        // Could use LLM to modify parent's prompt
        return "Modified system prompt";
    }

    private string GetNearbyAgentsInfo()
    {
        string info = "";
        foreach (var agent in nearbyAgents)
        {
            float distance = Vector3.Distance(transform.position, agent.transform.position);
            info += $"- {agent.name} (Distance: {distance:F1} units)\n";
        }
        return string.IsNullOrEmpty(info) ? "None" : info;
    }

    private string GetAttackersInfo()
    {
        string info = "";
        foreach (var agent in nearbyAgents)
        {
            if (agent.isAttacking && agent.targetName == this.name)
            {
                info += $"- {agent.name}\n";
            }
        }
        return string.IsNullOrEmpty(info) ? "None" : info;
    }

    private string GetAlliesInfo()
    {
        string info = "";
        foreach (var ally in allies)
        {
            float distance = Vector3.Distance(transform.position, ally.transform.position);
            info += $"- {ally.name} (Distance: {distance:F1} units)\n";
        }
        return string.IsNullOrEmpty(info) ? "None" : info;
    }

    private string GetEnemiesInfo()
    {
        string info = "";
        foreach (var enemy in enemies)
        {
            float distance = Vector3.Distance(transform.position, enemy.transform.position);
            info += $"- {enemy.name} (Distance: {distance:F1} units)\n";
        }
        return string.IsNullOrEmpty(info) ? "None" : info;
    }

    void OnValidate()
    {
        // This gets called when inspector values change
        if (sendMessageButton)
        {
            sendMessageButton = false;  // Reset the button
            if (!string.IsNullOrEmpty(messageToSend) && !string.IsNullOrEmpty(receiverName))
            {
                SendMessage(receiverName, messageToSend);
                messageToSend = "";  // Clear the message field after sending
            }
        }
    }

    IEnumerator AIDecisionLoop()
    {
        while (aiActive)
        {
            var stats = GetComponent<EvolStats>();
            if (stats != null && stats.currentHealth > 0)
            {
                yield return new WaitForSeconds(aiThinkingInterval);
                _ = MakeAIDecision();
            }
            yield return null;
        }
    }

    async Task MakeAIDecision()
    {
        UpdateContextPrompt(); // This already exists in your code
        string prompt = GeneratePrompt();
        Debug.Log($"<{gameObject.name}> AI Prompt:\n{prompt}");
        string action = await GetAIResponse(prompt);
        Debug.Log($"<{gameObject.name}> AI Output: {action}");
        ProcessAIAction(action);
    }

    async Task<string> GetAIResponse(string prompt)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var messages = new List<MessageData>
            {
                new MessageData { role = "system", content = systemPrompt }
            };

            // Add message history for context
            string[] chatLines = globalChatHistory.Split('\n');
            foreach (string line in chatLines)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    messages.Add(new MessageData { role = "user", content = line });
                }
            }

            // Add current prompt
            messages.Add(new MessageData { role = "user", content = prompt });

            var request = new OpenAIRequest
            {
                messages = messages
            };

            var jsonRequest = JsonUtility.ToJson(request);
            var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
            var response = await client.PostAsync(openAIEndpoint, content);
            var responseString = await response.Content.ReadAsStringAsync();
            var openAIResponse = JsonUtility.FromJson<OpenAIResponse>(responseString);

            return openAIResponse.choices[0].message.content;
        }
    }

    private void ProcessAIAction(string action)
    {
        // Parse message command
        var messageMatch = System.Text.RegularExpressions.Regex.Match(action, @"\[message ([^:]+): ([^\]]+)\]");
        if (messageMatch.Success)
        {
            string targetName = messageMatch.Groups[1].Value;
            string message = messageMatch.Groups[2].Value;
            SendMessage(targetName, message);
            return;
        }

        // Parse attack command
        var attackMatch = System.Text.RegularExpressions.Regex.Match(action, @"\[attack ([^\]]+)\]");
        if (attackMatch.Success)
        {
            targetName = attackMatch.Groups[1].Value;
            Debug.Log($"<{gameObject.name}> Attempting to attack {targetName}");
        }
    }

    private string GeneratePrompt()
    {
        StringBuilder prompt = new StringBuilder();
        
        // Add basic info about self
        prompt.AppendLine($"You are {gameObject.name}. Your current state:");
        var stats = GetComponent<EvolStats>();
        if (stats != null)
        {
            prompt.AppendLine($"Health: {stats.currentHealth}/{stats.maxHealth}");
            prompt.AppendLine($"Attack Damage: {attackDamage}");
        }

        // Add info about current target
        if (!string.IsNullOrEmpty(targetName))
        {
            prompt.AppendLine($"\nCurrent Target: {targetName}");
        }

        // Add info about nearby entities
        prompt.AppendLine("\nNearby Agents:");
        foreach (var agent in nearbyAgents)
        {
            if (agent != null && agent != gameObject)
            {
                float distance = Vector3.Distance(transform.position, agent.transform.position);
                var agentStats = agent.GetComponent<EvolStats>();
                if (agentStats != null)
                {
                    prompt.AppendLine($"- {agent.name} (Health: {agentStats.currentHealth}/{agentStats.maxHealth}, Distance: {distance:F1})");
                }
            }
        }

        // Add recent chat history
        if (!string.IsNullOrEmpty(globalChatHistory))
        {
            prompt.AppendLine("\nRecent Messages:");
            prompt.AppendLine(globalChatHistory);
        }

        // Add context and instruction prompts
        prompt.AppendLine($"\n{contextPrompt}");
        prompt.AppendLine($"\n{instructionPrompt}");

        return prompt.ToString();
    }
} 