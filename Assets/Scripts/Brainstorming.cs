using UnityEngine;

public class Brainstorming : MonoBehaviour
{
    /* Minecraft-Type Landscape Generator
     * == 3 Phases ==
     * 1. Heights and Dips
     * 2. Low areas with water (transparent blue block)
     * 3. Simple path - Moves along blocks, avoids water(goes around), goes up hills (if block more than 1 up make tunnel around area 2-3 blocks up)
     * 
     * 
     * Random Y Range for heights and dips
     * Areas below set Y Range - fill with transparent blue blocks for water
     * Pathing replaces normal block generation - if headed to water blocks, path around them and continue on. If Blocks more than 1 high ^^^^
     * 
     */
    private void Start()
    {
        Mathf.PerlinNoise(0, 1);
    }
    
}
