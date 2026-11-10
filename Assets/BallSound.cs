using UnityEngine;

public class BallSound : MonoBehaviour
{
    private Rigidbody rb;
    private AudioSource audioSource;
    
    // Скорость, при которой звук будет максимально громким
    public float maxSpeed = 2f; 

    void Start()
    {
        rb = GetComponent<Rigidbody>();
        audioSource = GetComponent<AudioSource>();
    }

    void Update()
    {
        // Измеряем текущую физическую скорость шарика
        float speed = rb.linearVelocity.magnitude;
        
        // Плавно меняем громкость от 0 до 1 пропорционально скорости
        audioSource.volume = Mathf.Clamp01(speed / maxSpeed);
    }
}