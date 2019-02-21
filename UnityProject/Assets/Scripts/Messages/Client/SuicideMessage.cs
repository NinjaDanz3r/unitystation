﻿using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SuicideMessage : ClientMessage
{
	public static short MessageType = (short)MessageTypes.Suicide;

	public override IEnumerator Process()
	{
		Logger.Log("Player '" + SentByPlayer.Name +"' has committed suicide", Category.Health);

		var livingHealthBehaviour = SentByPlayer.Script.GetComponent<LivingHealthBehaviour>();
		livingHealthBehaviour.Death();
		yield return null;
	}


	/// <summary>
	/// Tells the server to kill the player that sent this message
	/// </summary>
	/// <param name="obj">Dummy variable that is required to make this signiture different
	/// from the non-static function of the same name. Just pass null. </param>
	/// <returns></returns>
	public static SuicideMessage Send(Object obj)
	{
		SuicideMessage msg = new SuicideMessage();
		msg.Send();
		return msg;
	}


}
