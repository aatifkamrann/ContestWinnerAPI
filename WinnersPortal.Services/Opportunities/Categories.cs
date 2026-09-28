using WinnersPortal.Services.Settings;
using WinnersPortal.Domain;

namespace WinnersPortal.Services.Opportunities;

/// <summary>
/// One kind of work an opportunity can be — a key that is stored and filtered on,
/// and a label people read. What decides which kind a brief is, and which
/// kinds a freelancer works in, is no longer a word list on this server: a
/// model reads the brief and the skills against this taxonomy.
///
/// <see cref="OpportunityCategory.Skills"/> is not that word list coming back. It
/// decides nothing and matches nothing — it is the vocabulary a member is
/// offered once they have said, themselves, which kind of work they do, so
/// that one skill is spelt one way across the portal and a profile does not
/// start from an empty box. What a kind of work is built with is a fact about
/// the trade, and a model call to be told that Android is written in Kotlin
/// would be a call spent on nothing. A subcategory's
/// <see cref="OpportunitySubcategory.Skills"/> are the same kind of list, narrower:
/// what the opportunity form offers first once a client has picked one.
/// </summary>
public sealed record OpportunitySubcategory(string Key, string Label, IReadOnlyList<string> Skills);

public sealed record OpportunityCategory(
    string Key,
    string Label,
    IReadOnlyList<OpportunitySubcategory> Subcategories,
    IReadOnlyList<string> Skills);

/// <summary>
/// The taxonomy the browse page filters by and the opportunity form fills from.
/// A registry rather than a table: the list is short, changes with the
/// product rather than with any one opportunity, and a key that is a constant
/// can be indexed, tested and read in a query. Adding a category is one
/// entry here and nothing else — the form, the filter, the suggestion and
/// the fit all read from it.
/// </summary>
public static class OpportunityCategories
{
    public const int MaxKey = Opportunity.MaxCategoryKey;

    public static readonly IReadOnlyList<OpportunityCategory> All =
    [
        new("web", "Web development",
            [
                new("frontend", "Front end",
                    ["HTML", "CSS", "JavaScript", "TypeScript", "React", "Next.js", "Vue", "Angular",
                     "Tailwind CSS", "Accessibility"]),
                new("backend", "Back end & APIs",
                    ["Node.js", "ASP.NET Core", "Django", "FastAPI", "Laravel", "Spring Boot",
                     "REST API design", "GraphQL", "PostgreSQL", "Redis"]),
                new("fullstack", "Full stack",
                    ["TypeScript", "React", "Next.js", "Node.js", "ASP.NET Core", "Laravel", "PostgreSQL",
                     "REST API design", "Docker", "Git"]),
                new("ecommerce", "E-commerce",
                    ["Shopify", "WooCommerce", "Stripe", "Payments", "Magento", "Next.js",
                     "Product catalogues", "Checkout flows", "SEO"]),
                new("cms", "WordPress & CMS",
                    ["WordPress", "PHP", "WooCommerce", "Gutenberg blocks", "Elementor", "Headless CMS",
                     "Strapi", "Contentful", "SEO"]),
                new("dashboard", "Dashboards & admin panels",
                    ["React", "TypeScript", "Chart.js", "D3", "Data tables", "Role-based access",
                     "REST API design", "PostgreSQL", "Reporting"]),
                new("landing", "Landing pages & marketing sites",
                    ["HTML", "CSS", "Tailwind CSS", "Next.js", "Webflow", "SEO", "Responsive design",
                     "Page speed", "Analytics", "Figma"]),
                new("integration", "Integrations & automation",
                    ["REST API design", "Webhooks", "OAuth 2.0", "Zapier", "n8n", "Python", "Node.js",
                     "Web scraping", "Automation", "Stripe"]),
            ],
            [
                "JavaScript", "TypeScript", "React", "Next.js", "Node.js", "PHP", "Laravel",
                "WordPress", "Django", "ASP.NET Core", "PostgreSQL", "REST APIs", "Tailwind CSS",
            ]),
        new("mobile", "Mobile apps",
            [
                new("android", "Android",
                    ["Kotlin", "Java", "Jetpack Compose", "Android SDK", "Room", "Retrofit", "Firebase",
                     "Google Play release", "Push notifications"]),
                new("ios", "iOS",
                    ["Swift", "SwiftUI", "UIKit", "Core Data", "Combine", "Xcode", "App Store release",
                     "TestFlight", "Push notifications"]),
                new("crossplatform", "Cross-platform (Flutter, React Native)",
                    ["Flutter", "Dart", "React Native", "Expo", "TypeScript", "Firebase", "App Store release",
                     "Google Play release", "Offline sync"]),
                new("pwa", "Progressive web apps",
                    ["JavaScript", "TypeScript", "React", "Service workers", "Web App Manifest", "IndexedDB",
                     "Workbox", "Push notifications", "Offline sync"]),
            ],
            [
                "Kotlin", "Swift", "Flutter", "Dart", "React Native", "Jetpack Compose", "SwiftUI",
                "Android SDK", "Firebase", "App Store release", "Push notifications", "Offline sync",
            ]),
        new("data", "Data, AI & machine learning",
            [
                new("ml", "Machine learning & models",
                    ["Python", "scikit-learn", "PyTorch", "TensorFlow", "pandas", "NumPy", "Jupyter",
                     "Feature engineering", "Model evaluation", "MLflow"]),
                new("llm", "Chatbots & LLM apps",
                    ["Python", "LangChain", "LlamaIndex", "OpenAI API", "Prompt engineering", "RAG",
                     "Embeddings", "Vector databases", "Chatbots", "FastAPI"]),
                new("analytics", "Analytics & reporting",
                    ["SQL", "Power BI", "Tableau", "Looker Studio", "Excel", "Python", "pandas", "Dashboards",
                     "Data visualisation", "Reporting"]),
                new("engineering", "Data engineering & pipelines",
                    ["Python", "SQL", "Apache Airflow", "dbt", "Apache Spark", "Kafka", "ETL",
                     "Data modelling", "Snowflake", "BigQuery"]),
                new("vision", "Computer vision",
                    ["Python", "OpenCV", "PyTorch", "TensorFlow", "YOLO", "Image classification",
                     "Object detection", "Image segmentation", "OCR"]),
            ],
            [
                "Python", "pandas", "SQL", "PyTorch", "TensorFlow", "scikit-learn", "OpenCV",
                "LangChain", "Vector databases", "Power BI", "Apache Airflow", "dbt",
            ]),
        new("devops", "Cloud, DevOps & security",
            [
                new("cloud", "Cloud setup & migration",
                    ["AWS", "Azure", "Google Cloud", "Terraform", "Serverless", "IAM", "Networking",
                     "Cloud migration", "Cost optimisation"]),
                new("containers", "Docker & Kubernetes",
                    ["Docker", "Docker Compose", "Kubernetes", "Helm", "Container registries", "Nginx",
                     "Traefik", "Linux"]),
                new("cicd", "CI/CD & build pipelines",
                    ["GitHub Actions", "GitLab CI", "Azure DevOps", "Jenkins", "CI/CD", "Docker",
                     "Automated testing", "Release management"]),
                new("monitoring", "Monitoring & reliability",
                    ["Prometheus", "Grafana", "OpenTelemetry", "ELK stack", "Logging", "Alerting",
                     "Uptime monitoring", "Incident response"]),
                new("security", "Security & penetration testing",
                    ["Penetration testing", "OWASP Top 10", "Burp Suite", "Nmap", "Vulnerability assessment",
                     "Threat modelling", "Security audits", "Hardening"]),
            ],
            [
                "Linux", "Docker", "Kubernetes", "Terraform", "AWS", "Azure", "GitHub Actions",
                "Nginx", "Ansible", "Prometheus", "Grafana", "Penetration testing",
            ]),
        new("desktop", "Desktop, systems & embedded",
            [
                new("windows", "Windows applications",
                    ["C#", ".NET", "WPF", "WinForms", "WinUI", "SQL Server", "Windows services", "Installers"]),
                new("crossdesktop", "Cross-platform desktop",
                    ["Electron", "Tauri", "Qt", "C++", "Rust", "TypeScript", ".NET MAUI", "Auto-update"]),
                new("cli", "CLI tools & scripts",
                    ["Python", "Go", "Rust", "Node.js", "Bash", "PowerShell", "Command-line tools",
                     "Automation"]),
                new("embedded", "Embedded & IoT",
                    ["Embedded C", "C++", "Arduino", "Raspberry Pi", "ESP32", "FreeRTOS", "MQTT", "Firmware",
                     "IoT"]),
            ],
            [
                "C#", ".NET", "WPF", "C++", "Qt", "Rust", "Go", "Electron", "PowerShell",
                "Embedded C", "Arduino", "Raspberry Pi",
            ]),
        new("design", "Design & branding",
            [
                new("logo", "Logo design",
                    ["Logo design", "Adobe Illustrator", "Vector graphics", "Typography", "Colour theory",
                     "Brand identity", "Figma"]),
                new("brand", "Brand identity",
                    ["Brand identity", "Brand guidelines", "Logo design", "Typography", "Colour theory",
                     "Adobe Illustrator", "Adobe InDesign", "Figma"]),
                new("ui", "UI & UX design",
                    ["Figma", "UI design", "UX design", "UX research", "Wireframing", "Prototyping",
                     "Design systems", "User flows", "Accessibility"]),
                new("illustration", "Illustration & graphics",
                    ["Illustration", "Adobe Illustrator", "Adobe Photoshop", "Procreate", "Vector graphics",
                     "Icon design", "Character design", "Infographics"]),
                new("presentation", "Decks & presentations",
                    ["PowerPoint", "Google Slides", "Keynote", "Pitch decks", "Infographics",
                     "Data visualisation", "Typography", "Figma"]),
                new("video", "Video & motion",
                    ["After Effects", "Premiere Pro", "DaVinci Resolve", "Motion graphics", "Video editing",
                     "Animation", "Lottie", "Sound design"]),
            ],
            [
                "Figma", "Adobe Illustrator", "Adobe Photoshop", "UI design", "UX research",
                "Brand identity", "Logo design", "Prototyping", "Design systems", "Typography",
                "Illustration", "After Effects",
            ]),
        new("content", "Writing & documentation",
            [
                new("technical", "Technical writing & documentation",
                    ["Technical writing", "API documentation", "Markdown", "Docs-as-code", "OpenAPI",
                     "Docusaurus", "User guides", "Editing"]),
                new("copy", "Copywriting & marketing content",
                    ["Copywriting", "SEO", "Content strategy", "Blog writing", "Email marketing",
                     "Landing page copy", "Social media content", "Editing"]),
                new("translation", "Translation & localisation",
                    ["Translation", "Localisation", "Proofreading", "i18n", "Subtitling",
                     "Terminology management", "Editing"]),
            ],
            [
                "Technical writing", "Copywriting", "Editing", "API documentation", "Markdown",
                "Docs-as-code", "SEO", "Content strategy", "Translation", "Localisation",
            ]),
        new("games", "Games",
            [
                new("unity", "Unity",
                    ["Unity", "C#", "Game design", "Unity UI", "Physics", "Shaders", "Mobile games", "Blender"]),
                new("unreal", "Unreal Engine",
                    ["Unreal Engine", "C++", "Blueprints", "Level design", "Shaders", "Niagara",
                     "Multiplayer networking", "3D modelling"]),
                new("webgame", "Web & mobile games",
                    ["JavaScript", "TypeScript", "Phaser", "PixiJS", "Three.js", "HTML5 Canvas", "Godot",
                     "Mobile games", "Game design"]),
            ],
            [
                "Unity", "C#", "Unreal Engine", "C++", "Godot", "Blender", "Game design",
                "Shaders", "3D modelling", "Multiplayer networking",
            ]),
        new("blockchain", "Blockchain & Web3",
            [
                new("contracts", "Smart contracts",
                    ["Solidity", "Smart contracts", "Ethereum", "Hardhat", "Foundry", "OpenZeppelin",
                     "Token standards", "Gas optimisation", "Contract auditing"]),
                new("dapps", "dApps & wallets",
                    ["ethers.js", "Web3.js", "wagmi", "React", "Wallet integration", "WalletConnect",
                     "Ethereum", "Solana", "IPFS"]),
            ],
            [
                "Solidity", "Smart contracts", "Ethereum", "Hardhat", "ethers.js", "Web3.js",
                "Rust", "Solana", "Token standards", "Contract auditing",
            ]),
        new("qa", "Testing & QA",
            [
                new("automation", "Test automation",
                    ["Playwright", "Cypress", "Selenium", "Jest", "TypeScript", "API testing", "Postman",
                     "Page Object Model", "CI/CD"]),
                new("manual", "Manual QA & test plans",
                    ["Manual QA", "Test plans", "Test cases", "Exploratory testing", "Regression testing",
                     "Acceptance testing", "Bug reporting", "Jira"]),
                new("performance", "Performance & load testing",
                    ["JMeter", "k6", "Gatling", "Locust", "Load testing", "Stress testing",
                     "Performance profiling", "Performance tuning"]),
            ],
            [
                "Playwright", "Cypress", "Selenium", "Jest", "Test plans", "Manual QA",
                "API testing", "Postman", "JMeter", "Load testing", "Bug reporting",
            ]),
        new("other", "Something else", [], []),
    ];

    private static readonly Dictionary<string, OpportunityCategory> ByKey =
        All.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static OpportunityCategory? Find(string? key) =>
        key is null ? null : ByKey.GetValueOrDefault(key.Trim().ToLowerInvariant());

    public static OpportunitySubcategory? FindSub(OpportunityCategory category, string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().ToLowerInvariant();
        return category.Subcategories.FirstOrDefault(s => s.Key == k);
    }

    /// <summary>
    /// Null when the pair is acceptable on a draft, else the message the form
    /// shows. A draft may carry no category at all; publish is where one
    /// becomes required (<see cref="PublishProblem"/>).
    /// </summary>
    public static string? Problem(string? category, string? subcategory)
    {
        if (string.IsNullOrWhiteSpace(category))
            return string.IsNullOrWhiteSpace(subcategory) ? null : "Pick a category before a subcategory.";
        var c = Find(category);
        if (c is null) return "Pick a category from the list.";
        if (!string.IsNullOrWhiteSpace(subcategory) && FindSub(c, subcategory) is null)
            return $"That subcategory is not under {c.Label} — pick one from its list.";
        return null;
    }

    public static string? PublishProblem(string? category) =>
        Find(category) is null
            ? "Pick a category before publishing — it is how entrants browse for this opportunity."
            : null;

    /// <summary>The stored form of a key: trimmed, lower-case, null for blank.</summary>
    public static string? CleanKey(string? key) =>
        string.IsNullOrWhiteSpace(key) ? null : key.Trim().ToLowerInvariant();

    /// <summary>
    /// Every key the model may answer with, in the order they are offered.
    /// A reading that names anything else is rejected rather than mapped:
    /// a category that is not in this list is not a category.
    /// </summary>
    public static IReadOnlySet<string> Keys =>
        All.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>Only the kinds an opportunity can actually be judged against — "something else" is nobody’s speciality.</summary>
    public static bool Judgeable(OpportunityCategory category) => category.Subcategories.Count > 0;
}
