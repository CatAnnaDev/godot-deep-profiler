# Deep Profiler

Addon de profiling C# pour Godot 4.7 (mono). Il combine :

- un **panneau editeur** (dock bas) qui recoit les donnees du jeu en cours d'execution via le debugger,
- un **overlay in-game** identique, utilisable en build exporte, sans editeur,
- une **API d'instrumentation** (`Prof.Scope`, compteurs, marqueurs) sans allocation dans la boucle chaude,
- un **explorateur d'objets profond** : arbre de scene, proprietes, ressources, connexions de signaux, poids memoire retenu, avec navigation par double clic.

## Installation

1. Copier `addons/deep_profiler/` dans le projet.
2. Compiler la solution C# (`dotnet build` ou l'editeur).
3. Projet > Parametres du projet > Extensions : activer **Deep Profiler**.
   L'activation ajoute l'autoload `DeepProf` (`res://addons/deep_profiler/Runtime/ProfilerRuntime.cs`).
   La desactivation le retire.
4. Lancer le jeu : le dock **Deep Profiler** se remplit, `F3` ouvre l'overlay dans le jeu.

Si le projet declare deja les plugins actives dans `project.godot` sans passer par l'interface,
ajouter l'autoload manuellement :

```
[autoload]
DeepProf="*res://addons/deep_profiler/Runtime/ProfilerRuntime.cs"
```

## Panneau editeur

Barre d'outils : etat de connexion, pause de la capture, frequence d'envoi (2 a 30 Hz),
capture des scopes, crawl du graphe d'objets, `GC.Collect` force, echelle de temps du jeu,
pause du jeu, affichage de l'overlay, export.

| Onglet | Contenu |
| --- | --- |
| Overview | Graphe principal multi-series, deux graphes secondaires (memoire, rendu) et la grille des 56 compteurs de la frame. Clic sur le graphe pour epingler une frame. |
| Scopes | Flame chart + arbre des scopes (total, self, appels, allocations, **objets moteur crees**, part de frame), vue plate triee par self, compteurs utilisateur, threads secondaires. |
| Heap | Allocation managee par type .NET, tailles par generation, fragmentation, pauses du collecteur, finaliseurs en attente. |
| Input | Classe des evenements dispatches, objets vivants pendant le dispatch, reglages qui multiplient les copies, diagnostic contextuel. |
| Scene | Arbre distant a expansion paresseuse (enfants, descendants, octets retenus, etat) et inspecteur complet a droite. |
| Objects | Recensement par classe avec verdict churn / fuite, deux colonnes de croissance (depuis le crawl precedent, depuis une baseline), auto crawl jusqu'a vingt fois par seconde, et la liste des instances vivantes d'une classe. |
| Resources | Toutes les ressources atteignables : chemin, classe, refcount, taille estimee et **qui les detient** (deplier une ligne). |
| Signals | Graphe complet des connexions emetteur / signal / recepteur / methode, filtrable, avec masquage des connexions internes au moteur. |
| Cost | Resultats des mesures d'ablation : cout en millisecondes **et en objets crees par frame** (voir plus bas). |
| Events | Marqueurs `Prof.Event`, pics de frame, collections forcees. |

Export : `user://deep_profiler/frames_*.csv` (toutes les frames, toutes les colonnes) et
`capture_*.json` (scopes, recensement, compteurs).

## Trouver ce qui cree des objets en masse

Trois angles complementaires, parce qu'aucun ne suffit seul.

**Le verdict.** L'onglet Objects compare le nombre d'objets moteur sur les dix dernieres secondes et tranche : *stable* (les objets naissent et meurent, c'est du churn, ca coute du temps d'allocation et pas de la memoire) ou *en croissance* (c'est une fuite). Il affiche aussi combien d'objets vivent **hors de l'arbre de scene** : s'ils y sont majoritaires, aucune table de classes ne pourra les nommer.

**La classe.** Si les objets sont atteignables, activer *Auto crawl* et trier par *Since crawl* : la classe qui accumule remonte en tete. Le mode *Light* compte les classes sans estimer la memoire, ce qui rend un intervalle de 0,25 s abordable ; l'intervalle effectif s'allonge tout seul si une passe coute plus du quart de l'intervalle demande.

**Le coupable.** Si les objets ne sont pas atteignables, la classe est introuvable par exploration : il faut bisecter. *Measure cost* sur un noeud donne les objets crees par frame avec et sans lui. On descend dans l'arbre jusqu'a celui dont le cout tombe a zero.

Cinq compteurs sont par ailleurs surveilles en continu (objets moteur, noeuds, ressources, noeuds orphelins, tas manage). Une croissance monotone sur douze secondes au dessus des seuils leve un evenement `growth` dans l'onglet Events, avec le debit par seconde.

Attention a une limite structurelle : le compteur d'objets moteur est un **net**. Ce qui est cree et libere dans la meme frame n'y apparait pas. C'est pour cela que l'onglet Input echantillonne **pendant** la phase de dispatch, et que l'ablation compte les creations plutot que les soldes.

## Evenements d'entree

Chaque evenement dispatche coute au minimum une copie : `Viewport::push_input` fabrique un exemplaire transforme avant meme d'appeler `_input`, et chaque viewport traverse en rajoute un. Le nombre d'objets suit donc le nombre d'evenements, pas ce que le code en fait.

L'onglet Input donne la classe exacte des evenements, leur cadence, les objets vivants au pic pendant le dispatch, le nombre de viewports dans le chemin d'entree, et l'etat de `use_accumulated_input` et `agile_event_flushing`. La case *Accumulate motion* bascule le reglage dans le jeu en direct, ce qui permet de mesurer l'effet immediatement.

Mesure sur la scene de demonstration, vingt mouvements de souris injectes par frame :

| | evenements par frame | objets pendant l'input |
| --- | --- | --- |
| accumulation desactivee | 20,0 | 38,0 |
| accumulation activee | 1,0 | 0,0 |

Si l'accumulation doit rester desactivee pour la precision de visee, il reste a reduire le cout par evenement : consommer la motion dans `_input` puis appeler `get_viewport().set_input_as_handled()` quand la souris est capturee, et poser `gui_disable_input` sur les viewports qui n'ont pas besoin de la souris.

## Overlay in-game

`F3` (configurable) affiche un panneau deplacable et redimensionnable, avec les memes vues :
Stats, Graph, Scopes, Heap, Input, Tree, Objects, Resources, Signals, Events.
Il fonctionne dans un build exporte, sans editeur ni debugger : les collectes sont faites en local.

L'overlay peut aussi encadrer un objet dans le jeu (`Highlight`) : rectangle projete a l'ecran
pour les `Control`, les `Node2D` et l'AABB des `Node3D` vus par la camera courante.

## API d'instrumentation

```csharp
using DeepProf;

using (Prof.Scope("Enemy.Think"))
{
    ...
}

private static readonly ProfMarker Marker = Prof.Marker("Enemy.Path");

public void Update()
{
    using (Prof.Scope(Marker))
        RecomputePath();

    Prof.Counter("enemies alive", count);
    Prof.CounterAdd("shots fired", 1);
    Prof.Event("wave", "vague 3 demarree");
}
```

- `Prof.Scope` renvoie une struct : aucun boxing, aucune allocation par appel.
- Les scopes reentrants sont comptes une seule fois (pas de double comptage recursif).
- Les allocations managees par scope sont mesurees via `GC.GetAllocatedBytesForCurrentThread`
  (desactivable par `deep_profiler/runtime/track_allocations`).
- Les threads secondaires ont leur propre arbre : appeler `Prof.ThreadTick()` de temps en temps
  pour publier leurs mesures vers le thread principal.
- Chaque scope compte aussi les objets moteur nes entre son entree et sa sortie, ce qui repond a
  la question du responsable d'une creation en masse. Desactivable par
  `deep_profiler/runtime/track_objects`.

## Mesure du cout d'un noeud (ablation)

Dans l'onglet Scene, bouton **Measure cost** : le noeud selectionne est desactive
(`ProcessMode.Disabled`) puis masque, sur quelques dizaines de frames chacune, et l'addon
compare les mesures. Le resultat separe le cout logique, le cout de rendu, et le nombre
d'objets moteur crees par frame avec et sans le noeud. Les temps sont compares en mediane,
robuste aux pics ; les creations d'objets en moyenne, car elles sont souvent sporadiques.
Les etats d'origine sont toujours restaures, y compris en cas d'erreur.

## Ce que les chiffres signifient

- **Frame** : temps mur entre deux fins d'etape idle, vsync incluse.
- **Process** / **Physics** : temps reel des etapes du jeu, mesure entre un noeud de priorite
  minimale et un noeud de priorite maximale. Les moniteurs `Performance` natifs de Godot donnent
  un maximum par seconde, pas une valeur par frame ; ils ne sont donc pas utilises pour ces deux la.
- **Other** : `Frame - Process - Physics`, c'est-a-dire rendu, serveurs et attente vsync.
- **Objects** (moniteur) compte tous les objets du moteur ; le recensement de l'onglet Objects
  compte les objets **atteignables depuis la racine**, ce qui est plus petit et plus utile.
- Les tailles marquees `~` sont des estimations (textures depuis format et mipmaps, maillages
  depuis le format de sommets, audio depuis duree et taux). Les images utilisent la taille exacte.
- Le sous-arbre du profiler lui-meme est exclu des recensements, des instances et des signaux.
- **Objects created** est un solde entre deux frames. La repartition *process / physics / ailleurs*
  indique dans quelle phase de la frame les objets apparaissent.
- Le jeu n'envoie rien tant que l'editeur ne s'est pas annonce. Sans plugin editeur charge, aucun
  message n'est emis et seul l'overlay fonctionne.

## Reglages du projet

| Cle | Defaut | Role |
| --- | --- | --- |
| `deep_profiler/runtime/enabled` | true | Coupe toute l'instrumentation. |
| `deep_profiler/runtime/capture_scopes` | true | Capture des scopes. |
| `deep_profiler/runtime/track_allocations` | true | Allocations managees par scope. |
| `deep_profiler/runtime/track_objects` | true | Objets moteur crees par scope. |
| `deep_profiler/runtime/history_frames` | 1800 | Frames gardees cote jeu. |
| `deep_profiler/runtime/send_rate_hz` | 10 | Frequence d'envoi vers l'editeur. |
| `deep_profiler/runtime/spike_ms` | 33 | Seuil de detection des pics. |
| `deep_profiler/crawl/max_objects` | 40000 | Budget d'exploration du graphe. |
| `deep_profiler/overlay/enabled` | true | Cree l'overlay au demarrage. |
| `deep_profiler/overlay/start_visible` | false | Overlay visible des le lancement. |
| `deep_profiler/overlay/hotkey` | F3 | Touche de bascule. |

## Scene de demonstration

`demo/Demo.tscn` : terrain, 240 cubes animes, caisses statiques, barils physiques,
particules ephemeres, HUD, et un controleur joueur.

- clic pour capturer la souris, `WASD` (positions physiques, donc `ZQSD` en AZERTY), `Shift` sprint, `Espace` saut
- souris pour viser, un `RayCast3D` interroge en permanence ce qui est vise
- clic gauche pousse l'objet vise, clic droit tire un projectile physique
- `E` ouvre l'objet vise directement dans l'inspecteur du profiler
- `F` l'encadre dans la scene
- `Echap` libere la souris, `F3` ouvre l'overlay
- `L` declenche une fuite deliberee (buffers dans un dictionnaire et noeuds orphelins jamais liberes)
  pour voir la detection de croissance, les deltas de recensement et l'onglet Heap reagir. Re-presser
  `L` libere tout.

## Structure

```
addons/deep_profiler/
  ProfilerPlugin.cs          plugin editeur, reglages, autoload
  Editor/                    dock, plugin debugger, source distante
  Runtime/                   autoload, echantillonnage, scopes, graphe d'objets, ablation
  Runtime/Overlay/           overlay in-game, surbrillance, source locale
  Shared/                    protocole, tampon circulaire, vues et controles partages
```

Les panneaux (`SceneTreePane`, `ObjectInspectorPane`, `ScopePane`, `CensusPane`, `ResourcePane`,
`SignalPane`, `EventPane`, `GraphControl`, `FlameChart`) sont partages : l'editeur les alimente
par messages du debugger, l'overlay par appels directs, via la meme interface `IGraphSource`.
