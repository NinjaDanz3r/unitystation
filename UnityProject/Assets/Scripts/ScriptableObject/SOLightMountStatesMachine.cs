﻿using System;
using System.Collections.Generic;
using UnityEngine;
using System.Text.RegularExpressions;

[CreateAssetMenu(fileName = "SOLightMountStatesMachine", menuName = "ScriptableObjects/States/SOLightMountStatesMachine", order = 0)]
public class SOLightMountStatesMachine : UnityEngine.ScriptableObject
{
	public SOLightMountState[] LightMountStates;
}
