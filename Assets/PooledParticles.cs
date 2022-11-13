using System;
using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(ParticleSystem))]
public class PooledParticles : MonoBehaviour
{
    [NonSerialized]
    private ParticleSystem _particles;
    public ParticleSystem Particles => _particles ??= GetComponent<ParticleSystem>();

    [NonSerialized]
    private Queue<PooledParticles> pool;

    [NonSerialized]
    private PooledParticles sourcePool;

    private void OnGameReload()
    {
        pool.Clear();
    }


    public ParticleSystem Spawn(Vector3 position, Quaternion rotation)
    {
        if (pool == null)
        {
            pool = new Queue<PooledParticles>();
            Submarine.OnGameReload += OnGameReload;
        }

        while (pool.TryDequeue(out PooledParticles particleSystem))
        {
            if (particleSystem == null)
            {
                continue;
            }
            //var instance = particleSystem;
            particleSystem.gameObject.SetActive(true);
            particleSystem.transform.SetPositionAndRotation(position, rotation);
            particleSystem.sourcePool = this;
            return particleSystem.Particles;
        }

        ParticleSystem instance = GameObject.Instantiate(Particles, position, rotation);
        instance.gameObject.SetActive(true);
        instance.GetComponent<PooledParticles>().sourcePool = this;
        return instance;
    }

    private void OnParticleSystemStopped()
    {
        gameObject.SetActive(false);
        /*if (pool == null)
        {
            pool = new Queue<PooledParticles>();
        }*/
        sourcePool.pool.Enqueue(this);
    }
}
