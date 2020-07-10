﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Serialization;

public class EscapeShuttle : NetworkBehaviour
{
	public MatrixInfo MatrixInfo => mm.MatrixInfo;
	private MatrixMove mm;

	private CentComm centComm;

	public ShuttleStatusEvent OnShuttleUpdate = new ShuttleStatusEvent();
	public ShuttleTimerEvent OnTimerUpdate = new ShuttleTimerEvent();

	/// <summary>
	/// Orientation for docking at station, eg Up if north to south.
	/// </summary>
	public OrientationEnum orientationForDocking = OrientationEnum.Up;

	//Coord set in inspector
	public Vector2 stationDockingLocation;
	public Vector2 stationTeleportLocation;

	//Destination Stuff
	[HideInInspector]
	public Destination CentcomDest;
	private Destination StationDest;

	[HideInInspector]
	public Destination CentTeleportToCentDock;

	private Destination currentDestination;

	[Tooltip("If escape shuttle movement is blocked for longer than this amount of time, will end the round" +
	         " with the escape impossible ending.")]
	[SerializeField]
	private int escapeBlockTimeLimit = 10;

	///tracks how long escape shuttle movement has been blocked to see if ending should be triggered.
	private float escapeBlockedTime;
	private bool isBlocked;

	// Checks if shuttle has docked at station
	private bool HasShuttleDockedToStation = false;

	// Indicate if the shuttle really started moving toward station (It really starts moving in the StartMovingAtCount remaining seconds)
	private bool startedMovingToStation;

	public float DistanceToDestination => Vector2.Distance( mm.ServerState.Position, currentDestination.Position );

	/// <summary>
	/// used for convenient control with our coroutine extensions
	/// </summary>
	private Coroutine timerHandle;

	/// <summary>
	/// Seconds for shuttle call, affected by alert level
	/// </summary>
	public int InitialTimerSeconds
	{
		get => initialTimerSeconds;
		set => initialTimerSeconds = value;
	}
	[Range( 0, 2000 )] [SerializeField] private int initialTimerSeconds = 120;

	/// <summary>
	/// How many seconds should be left before arrival when recall should be blocked, affected by alert level
	/// </summary>
	public int TooLateToRecallSeconds
	{
		get => tooLateToRecallSeconds;
		set => tooLateToRecallSeconds = value;
	}
	[Range( 0, 1000 )] [SerializeField] private int tooLateToRecallSeconds = 60;

	/// <summary>
	/// Current "flight" time
	/// </summary>
	public int CurrentTimerSeconds
	{
		get => currentTimerSeconds;
		private set
		{
			currentTimerSeconds = value;
			OnTimerUpdate.Invoke( currentTimerSeconds );
		}
	}
	private int currentTimerSeconds = 0;

	/// <summary>
	/// Assign initial status via Editor
	/// </summary>
	public EscapeShuttleStatus Status
	{
		get => internalStatus;
		set
		{
			internalStatus = value;
			OnShuttleUpdate?.Invoke( internalStatus );
			Logger.LogTrace( gameObject.name + " EscapeShuttle status changed to " + internalStatus );
		}
	}

	/// <summary>
	/// True iff this shuttle has at least one functional thruster.
	/// </summary>
	public bool HasWorkingThrusters => thrusters != null && thrusters.Count > 0;

	/// <summary>
	/// tracks the thrusters we have so we can check for game over when it's immobilized.
	/// Note it's not currently possible to construct thrusters. This is only stored server side.
	/// Thrusters are removed from this when destroyed
	/// </summary>
	private List<ShipThruster> thrusters = new List<ShipThruster>();

	[SerializeField] private EscapeShuttleStatus internalStatus = EscapeShuttleStatus.DockedCentcom;

	private void Start()
	{
		switch (orientationForDocking)
		{
			case OrientationEnum.Right:
				CentcomDest = new Destination { Orientation = Orientation.Right, Position = stationTeleportLocation};
				StationDest = new Destination { Orientation = Orientation.Right, Position = stationDockingLocation};
				break;
			case OrientationEnum.Up:
				CentcomDest = new Destination { Orientation = Orientation.Up, Position = stationTeleportLocation};
				StationDest = new Destination { Orientation = Orientation.Up, Position = stationDockingLocation};
				break;
			case OrientationEnum.Left:
				CentcomDest = new Destination { Orientation = Orientation.Left, Position = stationTeleportLocation};
				StationDest = new Destination { Orientation = Orientation.Left, Position = stationDockingLocation};
				break;
			case OrientationEnum.Down:
				CentcomDest = new Destination { Orientation = Orientation.Down, Position = stationTeleportLocation};
				StationDest = new Destination { Orientation = Orientation.Down, Position = stationDockingLocation};
				break;
		}

		centComm = GameManager.Instance.GetComponent<CentComm>();
	}

	public void InitDestination(Vector3 newPos)
	{
		CentTeleportToCentDock = new Destination { Orientation = Orientation.Right, Position = newPos};
	}

	private void Awake()
	{
		mm = GetComponent<MatrixMove>();

		thrusters = GetComponentsInChildren<ShipThruster>().ToList();
		foreach (var thruster in thrusters)
		{
			var integrity = thruster.GetComponent<Integrity>();
			integrity.OnWillDestroyServer.AddListener(OnWillDestroyThruster);
		}
	}

	//called when each thruster is destroyed
	private void OnWillDestroyThruster(DestructionInfo destruction)
	{
		if (!CustomNetworkManager.IsServer) return;
		thrusters.Remove(destruction.Destroyed.GetComponent<ShipThruster>());

		if (thrusters.Count == 0)
		{
			ServerStartStrandedEnd();
		}
	}

	private void ServerStartStrandedEnd()
	{
		//game over! escape shuttle has no thrusters so it's not possible to reach centcomm.
		currentDestination = Destination.Invalid;
		RpcStrandedEnd();
		StartCoroutine(WaitForGameOver());
		GameManager.Instance.RespawnCurrentlyAllowed = false;
	}

	IEnumerator WaitForGameOver()
	{
		//note: used to wait for 25 seconds, now less because
		//we disabled the zoom out
		yield return WaitFor.Seconds(15f);
		// Trigger end of round
		GameManager.Instance.EndRound();
	}

	[ClientRpc]
	private void RpcStrandedEnd()
	{
		UIManager.Instance.PlayStrandedAnimation();
	}

	private void Update()
	{
		if ( !CustomNetworkManager.Instance._isServer || currentDestination == Destination.Invalid )
		{
			return;
		}


		//arrived to destination
		if ( mm.ServerState.IsMoving )
		{

			if (DistanceToDestination < 200)
			{
				mm.SetSpeed(80);
			}

			if ( DistanceToDestination < 2 )
			{
				mm.SetPosition( currentDestination.Position );
				mm.StopMovement();

				//centcom docked state is set manually instead, as we should usually pretend that flight is longer than it is
				if ( Status == EscapeShuttleStatus.OnRouteStation )
				{
					Status = EscapeShuttleStatus.DockedStation;
					HasShuttleDockedToStation = true;
				}
				else if(Status == EscapeShuttleStatus.OnRouteToStationTeleport)
				{
					Status = EscapeShuttleStatus.OnRouteToCentCom;

					TeleportToCentTeleport();
				}
				else if(Status == EscapeShuttleStatus.OnRouteToCentCom)
				{
					Status = EscapeShuttleStatus.DockedCentcom;
					if (Status == EscapeShuttleStatus.DockedCentcom && HasShuttleDockedToStation == true)
					{
						SoundManager.PlayAtPosition("HyperSpaceEnd", transform.position, gameObject);
					}
				}
			}

			else if ( DistanceToDestination < 25)
			{
				TryPark();
			}
		}

		//check if we're trying to move but are unable to
		if (!isBlocked)
		{
			if (Status != EscapeShuttleStatus.DockedCentcom && Status != EscapeShuttleStatus.DockedStation)
			{
				if ((!mm.ServerState.IsMoving || mm.ServerState.Speed < 1f) && startedMovingToStation)
				{
					Logger.LogTrace("Escape shuttle is blocked.", Category.Matrix);
					isBlocked = true;
					escapeBlockedTime = 0f;
				}
			}
		}
		else
		{
			//currently blocked, check if we are unblocked
			if (Status == EscapeShuttleStatus.DockedCentcom || Status == EscapeShuttleStatus.DockedStation ||
			    (mm.ServerState.IsMoving && mm.ServerState.Speed >= 1f))
			{
				Logger.LogTrace("Escape shuttle is unblocked.", Category.Matrix);
				isBlocked = false;
				escapeBlockedTime = 0f;
			}
			else
			{
				//continue being blocked
				escapeBlockedTime += Time.deltaTime;
				if (escapeBlockedTime > escapeBlockTimeLimit)
				{
					Logger.LogTraceFormat("Escape shuttle blocked for more than {0} seconds, stranded ending playing.", Category.Matrix, escapeBlockTimeLimit);
					//can't escape
					ServerStartStrandedEnd();
				}
			}
		}
	}

	//sorry, not really clean, robust or universal
	#region parking

	private bool parkingMode = false;
	private bool isReverse = false;

	private void TryPark()
	{
		//slowing down
		if ( !parkingMode )
		{
			parkingMode = true;
			mm.SetSpeed( 2 );
		}

		if ( !isReverse )
		{
			isReverse = true;
			mm.ChangeFacingDirection(mm.ServerState.FacingDirection.Rotate(2));
			/*
			if (Status == ShuttleStatus.DockedStation)
			{
				PlaySoundMessage.SendToAll("ShuttleDocked", Vector3.zero, 1f);
			}
			else {}
			*/
			HasShuttleDockedToStation = true;
		}
	}

	private void RemovePark( ShuttleStatus unused )
	{
		if ( parkingMode )
		{
			mm.ChangeFlyingDirection(mm.ServerState.FacingDirection);
			isReverse = false;
		}

		parkingMode = false;
	}

	#endregion

	#region Moving To Station

	/// <summary>
	/// Calls the shuttle from afar.
	/// </summary>
	public bool CallShuttle(out string callResult, int seconds = 0)
	{
		startedMovingToStation = false;

		if ( Status != EscapeShuttleStatus.DockedCentcom )
		{
			callResult = "Can't call shuttle: not docked at Centcom!";
			return false;
		}

		var Alert = centComm.CurrentAlertLevel;

		//Changes EscapeShuttle time depending on Alert Level

		if (Alert == CentComm.AlertLevel.Green)
		{
			//Double the Time
			InitialTimerSeconds *= 2;
			TooLateToRecallSeconds *= 2;
		}
		else if (Alert == CentComm.AlertLevel.Blue)
        {
			//Default values set in inspector
		}
		else if (Alert == CentComm.AlertLevel.Red || Alert == CentComm.AlertLevel.Delta)
		{
			//Half the Time
			InitialTimerSeconds /= 2;
			TooLateToRecallSeconds /= 2;
		}


		//don't change InitialTimerSeconds if they weren't passed over
		if ( seconds > 0 )
		{
			InitialTimerSeconds = seconds;
		}

		CurrentTimerSeconds = InitialTimerSeconds;
		mm.StopMovement();
		Status = EscapeShuttleStatus.OnRouteStation;

		//start ticking timer
		this.TryStopCoroutine( ref timerHandle );
		this.StartCoroutine( TickTimer(), ref timerHandle );

		Debug.Log("time: " + Vector2.Distance(stationTeleportLocation, stationDockingLocation) / mm.MaxSpeed + 10f);

		//adding a temporary listener:
		//start actually moving ship if it's seconds before arrival is how much it moves by and it hasn't been recalled...
		void Action( int time )
		{
			//Time = Distance/Speed
			if ( time <= Vector2.Distance(stationTeleportLocation, stationDockingLocation) / mm.MaxSpeed + 10f)
			{
				mm.SetPosition(stationTeleportLocation);
				MoveToStation();
				OnTimerUpdate.RemoveListener( Action ); //self-remove after firing once
			}
		}

		OnTimerUpdate.AddListener( Action );

		//...otherwise above thing gets aborted and never executes
		OnShuttleUpdate.AddListener( newStatus =>
		{
			if ( newStatus != EscapeShuttleStatus.OnRouteStation )
			{
				OnTimerUpdate.RemoveListener( Action );
			}
		} );

		callResult = "Shuttle has been called.";
		return true;
	}

	public void MoveToStation()
	{
		startedMovingToStation = true;

		mm.SetSpeed( mm.MaxSpeed );
		MoveTo(StationDest);
	}

	#endregion

	#region Recall

	public bool RecallShuttle(out string callResult)
	{
		startedMovingToStation = false;

		if ( Status != EscapeShuttleStatus.OnRouteStation
		     || CurrentTimerSeconds < TooLateToRecallSeconds )
		{
			callResult = "Can't recall shuttle: not on route to Station or too late to recall!";
			return false;
		}

		this.TryStopCoroutine( ref timerHandle );
		this.StartCoroutine( TickTimer( true ), ref timerHandle );

		void Action( int time )
		{
			if ( time >= InitialTimerSeconds )
			{
				Status = EscapeShuttleStatus.DockedCentcom;
				OnTimerUpdate.RemoveListener( Action ); //self-remove after firing once
			}
		}

		OnTimerUpdate.AddListener( Action );
		OnShuttleUpdate.AddListener( newStatus =>
		{
			if ( newStatus != EscapeShuttleStatus.OnRouteToCentCom )
			{
				OnTimerUpdate.RemoveListener( Action );
			}
		} );

		mm.StopMovement();
		Status = EscapeShuttleStatus.OnRouteToCentCom;

		mm.SetPosition( CentTeleportToCentDock.Position - new Vector3(1000,0,0) );
		mm.SetSpeed( 90 );
		MoveTo(CentTeleportToCentDock);

		callResult = "Shuttle has been recalled.";
		return true;
	}

	#endregion


	#region Moving To CentCom

	public void SendShuttle()
	{
		SoundManager.PlayAtPosition("HyperSpaceBegin", transform.position, gameObject);

		StartCoroutine(WaitForShuttleLaunch());
	}

	IEnumerator WaitForShuttleLaunch()
	{
		yield return WaitFor.Seconds(7f);

		SoundManager.PlayAtPosition("HyperSpaceProgress", transform.position, gameObject);

		Status = EscapeShuttleStatus.OnRouteToStationTeleport;

		mm.SetSpeed(100f);
		mm.StartMovement();
		mm.MaxSpeed = 100f;
		MoveTo( CentcomDest );
	}

	/// <summary>
	/// Send shuttle to centcom immediately.
	/// Server only.
	/// </summary>
	public void MoveToCentcom()
	{
		mm.SetSpeed( 90 );
		MoveTo( CentcomDest );

	}

	public void TeleportToCentTeleport()
	{
		mm.StopMovement();
		mm.SetPosition(CentTeleportToCentDock.Position - new Vector3(1000,0,0) );
		MoveTo(CentTeleportToCentDock);
	}

	#endregion

	private IEnumerator TickTimer( bool inverse = false )
	{
		while ( inverse ? (CurrentTimerSeconds < InitialTimerSeconds) : (CurrentTimerSeconds > 0) )
		{
			if ( inverse )
			{
				CurrentTimerSeconds += 1;
			} else
			{
				CurrentTimerSeconds -= 1;
			}

			yield return WaitFor.Seconds( 1 );
		}
	}

	private void MoveTo( Destination dest )
	{
		currentDestination = dest;
		mm.AutopilotTo( currentDestination.Position );
	}
}

public enum EscapeShuttleStatus
{
	OnRouteStation = 0,
	DockedStation = 1,
	OnRouteToStationTeleport = 2,
	OnRouteToCentCom = 3,
	DockedCentcom = 4
}

public class ShuttleStatusEvent : UnityEvent<EscapeShuttleStatus> { }

public class ShuttleTimerEvent : UnityEvent<int> { }
