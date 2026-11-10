using UnityEngine;

public class WinTrigger : MonoBehaviour
{
    public GameObject winScreen; 

    void OnTriggerEnter(Collider other)
    {
        if (other.gameObject.name == "Sphere")
        {
            winScreen.SetActive(true);
        }
    }
}