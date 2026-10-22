# Minou

Le statut de Claude Code en direct, dans une encoche en haut de l'écran.

Inspiré de [NotchAgent](https://github.com/TeamNoSleepz/notch-agent) (macOS, MIT) et repensé pour Windows :
un seul `.exe` natif, aucune dépendance, compilé avec le compilateur C# fourni avec Windows.

## Ce que ça fait

| État | Encoche | Grille 3×3 |
|---|---|---|
| **Au repos** | fine pastille noire | — |
| **Claude travaille** | barre : outil en cours + détail (`Terminal · npm run build`) | traînée crème + halo orangé, motif tiré au hasard |
| **Validation requise** | barre : `Validation requise · projet · outil` | colonne centrale rouge |
| **Terminé** | flash `Terminé · projet` puis retour en pastille | ✓ vert |
| **Erreur API** | flash `Erreur · projet` | ✕ rouge |

- **Survol** : l'encoche se déplie et liste les sessions (projet, activité, durée). **Clic sur une session** :
  son terminal revient au premier plan (Windows Terminal, console classique, VS Code, IDE JetBrains…).
- **Compteur** de sessions à droite, sessions multiples agrégées (la validation en attente passe en priorité).
- **Icône animée dans la zone de notification** : clic gauche → saute à la session qui attend une validation ;
  clic droit → menu (sessions, écran, sons, options).
- **Sons Windows** optionnels quand Claude a besoin de toi ou termine.
- **Multi-écran** : écran principal, un écran précis, ou celui de la fenêtre active. Mise à l'échelle par écran (125 %, 150 %…).
- **Plein écran** : l'encoche se retire pendant un jeu, une vidéo ou une présentation.
- **Cycle de vie** : elle apparaît quand une session Claude Code démarre et se retire quand la dernière se ferme.
  Rien ne tourne au démarrage de Windows (option « Rester ouvert sans Claude Code » dans le menu).
- **Discrète** : laisse passer les clics hors de la forme, ne prend jamais le focus, absente de la barre des tâches
  et d'Alt+Tab ; ignore le bord haut pendant que tu fais glisser une fenêtre (Snap).

## Installation

Prérequis : Windows 10 ou 11, [Claude Code](https://claude.com/claude-code). Rien d'autre.

### Plugin Claude Code (recommandé)

Le dépôt est aussi une marketplace de plugins : le plugin embarque l'exécutable et ses hooks, Claude Code s'occupe
du reste (rien n'est écrit dans `settings.json`, et `/plugin uninstall` retire tout proprement).

```
/plugin install minou --marketplace 0xManusdev/minou
```

Ou en deux temps, depuis un terminal :

```
claude plugin marketplace add 0xManusdev/minou
claude plugin install minou@minou
```

Depuis une copie locale du dépôt :

```
claude plugin marketplace add D:/chemin/vers/minou
claude plugin install minou@minou
```

Puis `/reload-plugins` dans les sessions déjà ouvertes. N'utilise pas le plugin **et** `install.ps1` en même temps :
chaque événement arriverait deux fois (`Minou.exe --uninstall-hooks` retire les hooks de l'installation manuelle).

Mise à jour du plugin : `.\build.ps1` recompile `plugins\minou\app\Minou.exe` (quitter l'encoche avant,
l'exe en cours d'exécution est verrouillé), puis augmenter `version` dans `plugins/minou/.claude-plugin/plugin.json`.

### Installation manuelle

```powershell
powershell -ExecutionPolicy Bypass -File install.ps1
```

Le script :

1. compile `bin\Minou.exe` (`build.ps1`, .NET Framework 4.8 intégré à Windows) ;
2. l'installe dans `%LOCALAPPDATA%\Programs\Minou` (+ raccourci menu Démarrer) ;
3. ajoute les hooks dans `~/.claude/settings.json` sans toucher au reste
   (sauvegarde unique : `settings.json.before-minou`).

Relancer `install.ps1` met à jour. Les sessions déjà ouvertes rejoignent l'encoche à leur prochain événement.

## Désinstallation

```powershell
powershell -ExecutionPolicy Bypass -File uninstall.ps1
```

Retire d'abord les hooks (un chemin de hook mort provoquerait une erreur à chaque événement), puis l'application,
ses données et le raccourci.

## Fonctionnement

```
Claude Code ──hook (exec form, async)──▶ Minou.exe hook
                                           │ lit l'événement sur stdin
                                           │ remonte l'arbre des processus : claude.exe + fenêtre hôte
                                           ▼
                              \\.\pipe\Minou.<utilisateur>   (accès réservé à l'utilisateur)
                                           ▼
                                   Minou.exe (WPF)
                                   sessions ─▶ encoche + icône + sons
```

- **Un seul exécutable, deux rôles** : lancé avec `hook`, il relaie l'événement et quitte (~150 ms, en arrière-plan,
  jamais bloquant). Sans argument, c'est l'application (instance unique).
- **Exec form** : Claude Code lance l'exe directement, sans passer par Git Bash.
- **Ordre garanti** : les hooks asynchrones peuvent arriver dans le désordre ; chaque événement porte l'heure de
  lancement de son hook, et un événement plus ancien ne peut pas faire reculer l'état (un `PostToolUse` tardif
  après `Stop`, par exemple).
- **Sessions vivantes** : chaque session est liée à son `claude.exe` (PID + date de création, contre le recyclage
  des PID) ; elle disparaît dès que le processus se termine, même sans `/exit`. `/clear` remplace la session
  dans le même processus.
- **Redémarrage** : les sessions sont sauvegardées et restaurées si leur processus vit encore.
- **Lancement** : si l'app ne tourne pas, le hook la démarre hors du job object de Claude Code
  (`CREATE_BREAKAWAY_FROM_JOB`, sinon WMI) pour qu'elle survive au hook.

### Événements → états

| Événement | État |
|---|---|
| `UserPromptSubmit`, `PreToolUse`, `PostToolUse(Failure)`, `PreCompact`, `PostCompact` | Travaille |
| `SubagentStart` / `SubagentStop` | compteur d'agents (une session au repos avec des agents en arrière-plan reste « au travail ») |
| `PermissionRequest`, `Notification` (permission, question, agent en attente) | Validation requise |
| `SessionStart`, `Stop` | Au repos (`Stop` déclenche le flash « Terminé ») |
| `StopFailure` | Au repos + flash « Erreur » |
| `SessionEnd` | session retirée |

## Données

`%LOCALAPPDATA%\Minou\` : `settings.json` (réglages du menu), `sessions.json`, `minou.log`
(menu → « Ouvrir le journal »).

## Développement

```powershell
.\build.ps1                 # compile bin\Minou.exe
.\bin\Minou.exe        # lance l'app
'{"hook_event_name":"UserPromptSubmit","session_id":"demo","cwd":"C:\\projet"}' | .\bin\Minou.exe hook
```

| Fichier | Rôle |
|---|---|
| `src/Program.cs` | point d'entrée, installation/retrait des hooks |
| `src/HookRelay.cs` | mode `hook` : relais vers le pipe, lancement de l'app |
| `src/ProcessTree.cs` | `claude.exe` et fenêtre hôte à partir de l'arbre des processus |
| `src/SessionStore.cs` | machine à états des sessions, liveness, persistance |
| `src/NotchWindow.cs` | encoche : forme, ressorts, survol, liste des sessions |
| `src/GridIndicator.cs` | grille 3×3 et ses motifs |
| `src/Tray.cs` | icône de notification et menu |
| `src/App.cs` | assemblage, sons, serveur de pipe, cycle de vie |
| `src/Native.cs` | appels Win32 |

## Crédits

Motifs d'animation, silhouette de l'encoche et correspondance événements → états repris de
[TeamNoSleepz/notch-agent](https://github.com/TeamNoSleepz/notch-agent) (licence MIT).
# minou
