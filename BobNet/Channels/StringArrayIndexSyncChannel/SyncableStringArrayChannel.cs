using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading.Tasks;

namespace BobNet
{
	public class SyncableStringArrayChannel : NetChannel
	{
		//structure should be
		//uno: guid
		//dos: 0/1 (to distinguish string or int array)
		//tres: actual data (actual int or string array)

		public override SerializationResult<byte[]> SerializeData(object objData, NetClient client)
		{
			if (!(objData is SyncableStringArray))
				return new SerializationResult<byte[]>(false);

			SyncableStringArray data = (SyncableStringArray)objData;
			byte[] guidData = data.ID.ToByteArray();
			byte isIndexesData = (byte)(data.SendingIndexes ? 1 : 0);

			//todo: possibly rework this to use shorts instead of ints in packets, would be smaller
			byte[] itemData;
			if (data.SendingIndexes)
			{
				//encoding indexes
				itemData = new byte[data.Indexes.Length * sizeof(int)];
				for (int i = 0; i < data.Indexes.Length; i++)
					Array.Copy(BitConverter.GetBytes(data.Indexes[i]), 0, itemData, i * sizeof(int), sizeof(int));
			}
			else
			{
				//encoding strings + necessary indexes
				int fullLength = 0;
				byte[][] strData = new byte[data.Items.Length][];
				for (int i = 0; i < strData.Length; i++)
				{
					strData[i] = Encoding.Unicode.GetBytes(data.Items[i].String);
					fullLength += strData[i].Length;
				}

				int currentPosition = 0;
				itemData = new byte[fullLength + (strData.Length * (sizeof(int) + sizeof(byte)))];
				for (int i = 0; i < strData.Length; i++)
				{
					itemData[currentPosition] = (byte)(data.Items[i].Necessary ? 1 : 0);
					Array.Copy(BitConverter.GetBytes(strData[i].Length), 0, itemData, currentPosition + 1, sizeof(int));
					Array.Copy(strData[i], 0, itemData, currentPosition + sizeof(int) + 1, strData[i].Length);
					currentPosition += sizeof(int) + strData[i].Length + 1;
				}
			}

			byte[] fullData = new byte[guidData.Length + sizeof(byte) + itemData.Length];
			Array.Copy(guidData, 0, fullData, 0, guidData.Length);
			fullData[guidData.Length] = isIndexesData;
			Array.Copy(itemData, 0, fullData, guidData.Length + sizeof(byte), itemData.Length);

			return new SerializationResult<byte[]>(true, fullData);
		}

		public override SerializationResult<object> DeserializeData(byte[] data, NetClient client)
		{
			if (data[16] > 1 || data.Length < 17 || (data[16] == 1 && (data.Length - 17) % sizeof(int) != 0)) 
				return new SerializationResult<object>(false);

			SyncableStringArray ret = new SyncableStringArray();

			byte[] guidData = new byte[16];
			Array.Copy(data, 0, guidData, 0, guidData.Length);
			ret.ID = new Guid(guidData);

			ret.SendingIndexes = data[guidData.Length] == 1 ? true : false;
			if (ret.SendingIndexes)
			{
				ret.Indexes = new int[(data.Length - 17) / sizeof(int)];
				for (int i = 0; i < ret.Indexes.Length; i++)
					ret.Indexes[i] = BitConverter.ToInt32(data, (i * sizeof(int)) + 17);
			}
			else
			{
				byte[] strData = new byte[data.Length - 17];
				Array.Copy(data, 17, strData, 0, strData.Length);

				var strings = new List<SyncableStringArray.OptionalString>();
				int i = 0;
				while (i < strData.Length)
				{
					bool necessary = strData[i] == 1 ? true : false;
					int strLen = BitConverter.ToInt32(strData, i + 1);
					byte[] strBuf = new byte[strLen];
					Array.Copy(strData, i + sizeof(int) + 1, strBuf, 0, strBuf.Length);

					strings.Add(new SyncableStringArray.OptionalString(Encoding.Unicode.GetString(strBuf), necessary));

					i += strLen + sizeof(int) + 1;
				}

				ret.Items = strings.ToArray();
			}

			return new SerializationResult<object>(true, ret);
		}
	}
}
