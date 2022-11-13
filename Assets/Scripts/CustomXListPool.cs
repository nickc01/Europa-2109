using System;
using System.Collections;
using System.Collections.Generic;

public static class CustomXListPool
{
    private class ArrayCollectionWrapper<T> : ICollection<T>
    {
        public static ArrayCollectionWrapper<T> Instance = new ArrayCollectionWrapper<T>();

        public T[] WrapperArray = null;
        public int Length = 0;


        public int Count => Length;

        public bool IsReadOnly => false;

        public void Add(T item)
        {
            throw new NotImplementedException();
        }

        public void Clear()
        {
            throw new NotImplementedException();
        }

        public bool Contains(T item)
        {
            throw new NotImplementedException();
        }

        public void CopyTo(T[] array, int arrayIndex)
        {
            Array.Copy(WrapperArray, 0, array, arrayIndex, Length);
        }

        public IEnumerator<T> GetEnumerator()
        {
            throw new NotImplementedException();
        }

        public bool Remove(T item)
        {
            throw new NotImplementedException();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            throw new NotImplementedException();
        }
    }



    public static List<T> ToListPooled<T>(T[] array, int length)
    {
        List<T> list = Unity.VisualScripting.ListPool<T>.New();

        ArrayCollectionWrapper<T> wrapper = ArrayCollectionWrapper<T>.Instance;

        wrapper.WrapperArray = array;
        wrapper.Length = length;

        list.AddRange(wrapper);

        return list;
    }
}

