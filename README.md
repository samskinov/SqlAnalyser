# SqlAnalyzer

SqlAnalyzer est une bibliothèque .NET Framework 4.8 qui analyse des scripts T-SQL, extrait les objets référencés, résout leurs définitions dans SQL Server, reconstruit leurs dépendances, puis produit un contexte structuré prêt à être envoyé à une IA pour générer de la documentation technique.

Le dépôt contient deux projets:

- `SqlAnalyzer.Core` : la bibliothèque principale.
- `SqlAnalyzer.Console` : une démonstration en ligne de commande.

## Fonctionnalités

- Lecture de fichiers `.sql`.
- Découpage par lots `GO` et prise en compte des changements de contexte `USE`.
- Analyse du T-SQL via `Microsoft.SqlServer.TransactSql.ScriptDom`.
- Détection des références vers `VIEW`, `STORED PROCEDURE`, `FUNCTION`, `TVF` et, selon configuration, `TABLE`.
- Résolution des définitions dans `sys.objects`, `sys.schemas`, `sys.sql_modules` et des dépendances transitives via `sys.sql_expression_dependencies`.
- Formatage propre des définitions SQL avant génération du contexte.
- Production d’un bloc texte prêt à injecter dans un prompt IA.
- Journalisation des objets non résolus et des warnings de parsing.

## Prérequis

- Windows ou environnement capable d’exécuter .NET Framework 4.8.
- SQL Server accessible depuis la machine qui exécute l’outil.
- Droits de lecture sur les métadonnées des objets ciblés, idéalement `VIEW DEFINITION` sur les bases analysées.

## Structure du dépôt

- [SqlAnalyzer.sln](SqlAnalyzer.sln)
- [SqlAnalyzer.Core/SqlAnalyzer.Core.csproj](SqlAnalyzer.Core/SqlAnalyzer.Core.csproj)
- [SqlAnalyzer.Console/SqlAnalyzer.Console.csproj](SqlAnalyzer.Console/SqlAnalyzer.Console.csproj)
- [samples/sample_query.sql](samples/sample_query.sql)

## Compilation

Depuis la racine `SqlAnalyzer`:

```powershell
msbuild SqlAnalyzer.sln /p:Configuration=Release
```

Ou via Visual Studio 2022 en ouvrant simplement la solution.

## Utilisation en ligne de commande

Le projet console attend au minimum une base par défaut et un ou plusieurs fichiers SQL:

```powershell
SqlAnalyzer.Console.exe --server MYSERVER --database MyDatabase query.sql
```

### Authentification Windows

Si `--user` et `--password` ne sont pas fournis, la connexion utilise l’authentification Windows:

```powershell
SqlAnalyzer.Console.exe --server MYSERVER --database MyDatabase query.sql
```

### Authentification SQL

```powershell
SqlAnalyzer.Console.exe --server MYSERVER --database MyDatabase --user sa --password "Secret123!" query.sql
```

### Options supportées

- `--server <host>` : serveur SQL Server cible. Valeur par défaut: `.`.
- `--database <name>` : base/catalogue par défaut. Obligatoire.
- `--user <login>` : login SQL. Si absent, l’authentification Windows est utilisée.
- `--password <pwd>` : mot de passe SQL.
- `--depth <n>` : profondeur maximale de résolution récursive. Valeur par défaut: `5`.
- `--no-scripts` : n’inclut pas les scripts d’origine dans le contexte généré.
- `--output <file>` : écrit le contexte généré dans un fichier au lieu de stdout.

### Exemple complet

```powershell
SqlAnalyzer.Console.exe `
  --server MYSERVER `
  --database MyDatabase `
  --depth 3 `
  --output .\prompt-context.txt `
  .\samples\sample_query.sql
```

## Utilisation avec une application parente

La bibliothèque peut être intégrée directement dans une application existante. Si l’application parente possède déjà une `SqlConnection`, il est possible de la passer à l’orchestrateur.

```csharp
using Microsoft.Data.SqlClient;
using SqlAnalyzer.Core.Models;
using SqlAnalyzer.Core.Orchestration;

using (var connection = new SqlConnection(connectionStringFromParent))
{
    connection.Open();

    var orchestrator = new SqlAnalysisOrchestrator(
        connection,
        defaultDatabase: "MyDatabase",
        options: new AnalysisOptions
        {
            MaxDependencyDepth = 5,
            ResolveRecursiveDependencies = true,
            IncludeTables = false
        });

    AnalysisResult result = orchestrator.Analyse(new[]
    {
        @"C:\scripts\query.sql"
    });
}
```

Dans ce mode, la chaîne de connexion du parent sert de modèle. La librairie réutilise le serveur et l’authentification, puis change uniquement le catalogue si nécessaire pour résoudre les dépendances dans d’autres bases.

## Sortie générée

Le `PromptBuilder` produit un bloc délimité par:

```text
---SQL CONTEXT BEGIN---
...
---SQL CONTEXT END---
```

Ce bloc peut contenir:

- les scripts SQL d’origine,
- un graphe de dépendances lisible,
- les définitions SQL formatées,
- la liste des objets non résolus avec leur raison.

## Utiliser PromptBuilder

Après avoir obtenu un `AnalysisResult`, la classe `PromptBuilder` transforme le résultat en texte prêt à être inséré dans un prompt d’IA.

```csharp
using SqlAnalyzer.Core.Prompting;

var builder = new PromptBuilder();
string promptContext = builder.Build(
    result,
    includeOriginalScripts: true,
    includeUnresolved: true);
```

Paramètres utiles:

- `includeOriginalScripts`: inclut ou non les scripts SQL d’origine.
- `includeUnresolved`: affiche ou non la liste des références non résolues.

Le texte retourné contient toujours les sections utiles au modèle:

- le bloc de délimitation `---SQL CONTEXT BEGIN---` et `---SQL CONTEXT END---`,
- les scripts source si demandé,
- le graphe des dépendances,
- les définitions résolues formatées,
- les références non résolues avec leur raison.

Dans le projet console, ce même builder est déjà utilisé après l’analyse pour produire la sortie affichée ou écrite dans le fichier passé à `--output`.

## Exemple de script

Le fichier [samples/sample_query.sql](samples/sample_query.sql) montre un exemple avec:

- `USE`
- `GO`
- références qualifiées dans plusieurs bases
- appel de vue, procédure stockée, fonction scalaire et table-valued function

## Limites connues

- Le SQL dynamique à l’intérieur des procédures n’est pas analysé via `sys.sql_expression_dependencies`.
- Les fonctions scalaires appelées sans schéma explicite peuvent ne pas être résolues.
- Les objets chiffrés ne renvoient pas leur définition.
- Les références liées à un linked server sont ignorées.
- Un accès en lecture aux métadonnées est nécessaire pour récupérer les définitions.

## Dépendances principales

- `Microsoft.Data.SqlClient`
- `Dapper`
- `Microsoft.SqlServer.TransactSql.ScriptDom`

## Bonnes pratiques d’usage

- Fournir le catalogue par défaut correct via `--database` ou `AnalysisOptions.DefaultDatabase`.
- Passer un `SqlConnection` déjà ouvert depuis l’application parente si celle-ci gère déjà l’authentification et le serveur.
- Conserver une profondeur de dépendance raisonnable si la base contient un grand nombre d’objets interconnectés.
- Vérifier les objets non résolus dans la section diagnostics avant d’utiliser le contexte généré.

## Dépannage rapide

- `--database is required.` : le catalogue par défaut n’a pas été fourni.
- `File not found` : le chemin du script est incorrect.
- `VIEW DEFINITION` manquant : la définition de certains objets reste vide ou marquée comme non disponible.
- Objet introuvable dans une autre base : vérifier que la connexion parente pointe bien vers le bon serveur et que l’utilisateur a accès à cette base.

## Licence

Aucune licence n’a été définie dans ce dépôt.