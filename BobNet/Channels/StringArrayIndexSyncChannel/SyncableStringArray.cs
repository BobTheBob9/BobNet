using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BobNet
{
	public struct SyncableStringArray
	{
		public struct OptionalString
		{
			public string String;
			public bool Necessary;

			public OptionalString(string str, bool necessary = false)
			{
				String = str;
				Necessary = necessary;
			}
		}

		public Guid ID;
		public bool SendingIndexes;
		public int[] Indexes;
		public OptionalString[] Items;

		public SyncableStringArray(Guid id, OptionalString[] items)
		{
			SendingIndexes = false;
			ID = id;
			Items = items;
			Indexes = null;
		}

		public SyncableStringArray(Guid id, string[] items)
		{
			SendingIndexes = false;
			ID = id;

			Items = new OptionalString[items.Length];
			for (int i = 0; i < items.Length; i++)
				Items[i] = new OptionalString(items[i]);

			Indexes = null;
		}

		public SyncableStringArray(Guid id, int[] indexes)
		{
			SendingIndexes = true;
			ID = id;
			Items = null;
			Indexes = indexes;
		}
	}
}
