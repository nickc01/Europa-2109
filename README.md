# Europa 2109

![Europa 2109 Screenshot](https://user-images.githubusercontent.com/12601671/223528564-6709173a-9a85-4a63-b395-9f59db375eec.PNG)

Europa 2109 is a small game where you are sent to the moon Europa to explore the moon's underwater cave systems. The goal of the game is to dig around and collect 5 red crystals to win the game.
The gmae is used to demonstrate several different techniques:
- Using a marching cubes algorithm to generate polygonal terrain. This is based off a [tutorial made by Sebastian Lague](https://www.youtube.com/watch?v=M3iI2l0ltbE)
- Using a 3D chunk system to only render terrain that's near the player, and to allow for terrain of nearly infinite size.
- Using multithreading via the C# Parallel namespace, async Tasks, and ComputeShaders to speed up the terrain generation process and the digging

You control a probe inside the ocean of Europa

Your goal is to find 5 crystals for the scientists to analyze

Use WASD or Arrow Keys to Move

Move mouse to rotate

Left click to dig

Hold C to collect
