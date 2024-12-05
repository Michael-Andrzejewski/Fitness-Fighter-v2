using UnityEngine;
using System.Collections.Generic;
using UnityEngine.AI;
using System.Collections;
using System.Text;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System;

public class AI_ControllerBehavior : MonoBehaviour
{
    [Header("AI State")]
    public bool isAttacking = false;
    public string targetName;

    [Header("AI Prompts")]
    [TextArea(3, 10)]
    public string systemPrompt = "You are an AI agent in a combat simulation. You can communicate with other agents and engage in combat. Your responses should reflect your personality and strategic thinking. Focus on building alliances when possible, but be ready to defend yourself.";
    [TextArea(3, 10)]
    public string instructionPrompt = "You may take ONE of the following actions. Respond with exactly ONE action in brackets:\n" +
        "[message agentName: Your message] - to communicate with another agent\n" +
        "[attack agentName] - to engage in combat with an agent\n" +
        "Consider your relationships, current health, and tactical situation when deciding.";
    [TextArea(3, 10)]
    public string contextPrompt = "";

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

    [Header("OpenAI Configuration")]
    private readonly string openAIEndpoint = "https://api.openai.com/v1/chat/completions";
    [SerializeField, Tooltip("Your OpenAI API Key")]
    private string apiKey;

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

        // Set the AI agent prefab to itself if not already assigned
        if (aiAgentPrefab == null)
        {
            aiAgentPrefab = gameObject;
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
                       $"Attackers:\n{GetAttackersInfo()}\n\n" +
                       $"Current Target You Are Attacking:\n{(string.IsNullOrEmpty(targetName) ? "None" : targetName)}";

        // Only include allies and enemies info if not tagged as Enemy
        if (!gameObject.CompareTag("Enemy"))
        {
            contextPrompt += $"\nAllies:\n{GetAlliesInfo()}\n" +
                            $"Enemies:\n{GetEnemiesInfo()}";
        }
    }

    private async void DecideAction()
    {
        // Build the complete prompt
        StringBuilder promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(systemPrompt);
        promptBuilder.AppendLine("\nInstructions:");
        promptBuilder.AppendLine(instructionPrompt);
        promptBuilder.AppendLine("\nContext:");
        promptBuilder.AppendLine(contextPrompt);

        // Print the full prompt
        Debug.Log($"<{gameObject.name}> Full AI Prompt:\n{promptBuilder}");

        try
        {
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

                var messages = new List<MessageData>
                {
                    new MessageData { role = "system", content = systemPrompt },
                    new MessageData { role = "user", content = $"{instructionPrompt}\n\n{contextPrompt}" }
                };

                var request = new OpenAIRequest
                {
                    messages = messages
                };

                var jsonRequest = JsonUtility.ToJson(request);
                var content = new StringContent(jsonRequest, Encoding.UTF8, "application/json");
                var response = await client.PostAsync(openAIEndpoint, content);
                var responseString = await response.Content.ReadAsStringAsync();
                
                Debug.Log($"<{gameObject.name}> Raw API Response: {responseString}"); // Debug line
                
                var openAIResponse = JsonUtility.FromJson<OpenAIResponse>(responseString);
                
                if (openAIResponse == null || openAIResponse.choices == null || openAIResponse.choices.Length == 0)
                {
                    Debug.LogError($"<{gameObject.name}> Invalid response format from API");
                    return;
                }

                if (openAIResponse.choices[0].message == null)
                {
                    Debug.LogError($"<{gameObject.name}> No message in API response");
                    return;
                }

                string aiOutput = openAIResponse.choices[0].message.content;
                Debug.Log($"<{gameObject.name}> AI Response:\n{aiOutput}");

                // TODO: Process the AI's response to take actions
                // This will be implemented in a future update
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"<{gameObject.name}> Error in DecideAction: {e.Message}\nStack Trace: {e.StackTrace}");
        }
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
        // Find all AI agents in the scene
        AI_ControllerBehavior[] allAgents = FindObjectsOfType<AI_ControllerBehavior>();
        nearbyAgents.Clear(); // Clear the existing list
        
        StringBuilder info = new StringBuilder();
        foreach (var agent in allAgents)
        {
            // Skip if it's this agent, is dead, or has no stats
            if (agent == this || agent.CompareTag("Dead") || agent.GetComponent<EvolStats>()?.killed == true)
                continue;
            
            // Add to nearby agents list
            nearbyAgents.Add(agent);
            
            // Calculate distance and add to info string
            float distance = Vector3.Distance(transform.position, agent.transform.position);
            info.AppendLine($"- {agent.name} (Distance: {distance:F1} units)");
        }
        
        return info.Length == 0 ? "None" : info.ToString();
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
} 