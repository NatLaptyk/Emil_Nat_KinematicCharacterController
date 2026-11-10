# Emil_Nat_KinematicCharacterController

A third-person character controller for a cat, built on the Kinematic Character Controller (KCC) asset. 
The motor handles collisions and grounding; `KittyCharacterController.cs` decides how the cat moves, jumps and climbs.

## Requirements

- Input System, Cinemachine 3 (installed through the Package Manager)
- Kinematic Character Controller (included under `Assets/Plugins`)

## How to run

1. Open the project in Unity.
2. Open `Assets/Emil_Nat_Kinematic_Character_Controller/TestScene.unity`.
3. Press Play.

## Controls

| Action | Keyboard and mouse | Gamepad |
| --- | --- | --- |
| Move | W A S D or arrow keys | Left stick |
| Look around | Mouse | Right stick |
| Run (hold) | Left Shift | Left stick press |
| Jump | Space | South button (A / Cross) |
| Air jump | Space while in the air | South button while in the air |
| Grab or let go of a ladder | E | North button (Y / Triangle) |
| Climb up and down | W / S | Left stick up / down |
| Leap to the next ladder | A or D + Space while on a ladder | Left stick left / right + South button |

## What the cat can do

**Move and run.** Movement is relative to the camera. The cat speeds up and slows down smoothly instead of snapping to full speed. 
Holding Run raises the top speed on the ground and in the air, so a running jump carries further.

**Jump.** Three things make the jump forgiving:

- Coyote time: a jump pressed just after walking off a ledge still counts.
- Jump buffering: a jump pressed just before landing fires on touchdown.
- Air jump: X extra jumps in the air. Holding a direction while pressing it sends the cat that way, so it can change direction mid-air.

**Climb.** Stand at a ladder (one of the trees in the scene) and press the climb button [E]. 
The cat slides onto the ladder and faces it. Push up or down to climb. 
At the top or bottom, the cat steps off by himself.

**Leap between ladders.** While on a ladder, hold left or right and press Jump. The cat throws himself sideways, still facing the wall, and catches the next ladder he touches. If he misses, he falls.

## Tuning

Every speed, strength and timing is a field on the `KittyCharacterController` component in the Inspector: walk and run speed, acceleration, jump and air jump strength, coyote time, jump buffer time, number of air jumps, climb speed and leap speed.

## Project layout

- `Assets/Emil_Nat_Kinematic_Character_Controller/AssignmentScripts/` — `KittyCharacterController.cs` and `KittyLadder.cs`
- `Assets/Emil_Nat_Kinematic_Character_Controller/Kitty/` — the cat model, textures, animations and Animator Controller
- `Assets/Emil_Nat_Kinematic_Character_Controller/TestScene.unity` — the test level

## Credits

- Fantasy Forest Environment Free Sample (environment art)
- Unity Input System and Cinemachine
