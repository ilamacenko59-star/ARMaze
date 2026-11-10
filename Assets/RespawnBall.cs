using UnityEngine;

public class RespawnBall : MonoBehaviour
{
    private Vector3 startPos;
    private Rigidbody rb;

    void Start()
    {
        // Запоминаем стартовую позицию шарика при запуске
        startPos = transform.localPosition;
        rb = GetComponent<Rigidbody>();
    }

    void Update()
    {
        // Если шарик провалился далеко вниз или улетел высоко вверх относительно лабиринта
        if (transform.localPosition.y < -0.5f || transform.localPosition.y > 1.5f)
        {
            // Возвращаем его на старт и полностью гасим скорость, чтобы он не прыгал
            transform.localPosition = startPos;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }
    }
}