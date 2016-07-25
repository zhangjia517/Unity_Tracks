/**
 * <copyright>
 * Tracks and Rails Asset Package by Zen Fulcrum
 * Copyright © 2015 Zen Fulcrum LLC
 * Usage is subject to Unity's Asset Store EULA (https://unity3d.com/legal/as_terms)
 * </copyright>
 */
namespace ZenFulcrum.Track {

using UnityEngine;
using System.Collections;

/**
 * A 3D curve with rotation along its length.
 * 
 * All curves start at (0, 0, 0) heading toward +z. (That is, all the positions you can get from this class
 * are in local coordinates.)
 */
public class TrackCurve {
	public enum CurveType {
		/*
		 * These types have been removed because they do not guarantee that the tangent of the curve matches the 
		 * direction of the curve at the start and end points (CatmullRom) or anywhere at all (Linear):
		 * Linear = 0,
		 * CatmullRom = 1,
		 */
		
		Hermite = 2,
	}


	protected CurveType curveType;
	protected SimpleTransform end;
	protected float startStrength, endStrength;

	public TrackCurve(CurveType curveType, SimpleTransform end, float startStrength, float endStrength) {
		this.curveType = curveType;
		this.end = end;
		this.startStrength = startStrength;
		this.endStrength = endStrength;

		if (Vector3.Distance(Vector3.zero, end.position) < 0.000001f) {
			throw new System.ArgumentOutOfRangeException("This curve is a point. Move the start and end apart.");
		}
	}

	/** Returns the length of this section of track. (The track length — not the distance between start and end.) */
	public float Length {
		get { 
			//todolater: this is only an approximation right now :-/
			//Ideally, we'd do some magic math and returns a mathematically "exact" result.
			//Currently, we just measure a few points and sum their distances.
			//Practically, we should have a computed set of "key" points (more points on curves) and sum their distances.

			var steps = 5;
			var length = 0f;
			var pos = new Vector3(0, 0, 0);

			for (int i = 1; i <= steps; i++) {
				var nextPos = GetPointAt(i / (float)steps).position;
				length += (pos - nextPos).magnitude;
				pos = nextPos;
			}

			return length;
		}
	}


	/**
	 * Returns a value that:
	 *   If in [0, 1]: if the position can be mapped onto the curve. The returned value is a curve fraction for
	 *     where on the track is nearest.
	 *   If < 0: The given position appears to be before the start of the curve.
	 *   If > 1: The given position appears to be after the start of the curve.
	 *   
	 * Except when mapped onto the curve, the exact magnitude of the returned value is undefined and should not be used
	 * except to determine which side is nearer.
	 */
	public float GetFraction(Vector3 position) {
		//Put together a series of lines (for now, just a half dozen segments from the curve)
		//Also include rays cast from the start and end to infinity.

		//Determine which line segment position is closest to.
		//Use Vector3.Project and throw out distances that are not in the current segment
		//Return lerp start and end point of a segment percent along the track
		const int numPieces = 5;

		var closestFraction = 0f;
		var closestDistance = float.PositiveInfinity;

		var lastPos = GetPointAt(0);
		var lastPosFraction = 0f;

		//Debug.Log("Position is " + position);
		for (int i = 1; i <= numPieces; ++i) {
			var fraction = i / (float)numPieces;
			var pos = GetPointAt(fraction);

			var lineClosestFraction = NearestPartOfSegment(position, lastPos.position, pos.position);
			var lineDistance = DistanceFromSegment(position, lastPos.position, pos.position);

			//Debug.Log("Step " + i + " is nearest " + lineClosestFraction +
			//	"(" + (lastPosFraction + lineClosestFraction / (float)numPieces) + 
			//	") along the line at " + lineDistance);

			if (lineDistance < closestDistance) {
				//closer than what we have so far
				closestDistance = lineDistance;
				closestFraction = lastPosFraction + lineClosestFraction / (float)numPieces;
			}

			lastPosFraction = fraction;
			lastPos = pos;
		}

		//Debug.Log("Picked " + closestFraction + " at " + closestDistance + " away");


		//Count items exactly on the edge as over the edge.
		if (closestFraction == 0) closestFraction = -1;
		else if (closestFraction == 1) closestFraction = 2;



		return closestFraction;
	}


	#region Spline Math

	const float sqrt2 = 1.4142135623730950488016887242097f;
	
	public static Vector3 CalcHermite(Vector3 p1, Vector3 t1, Vector3 p2, Vector3 t2, float percent) {
		//http://cubic.org/docs/hermite.htm
		float s = percent, s2 = s * s, s3 = s2 * s;
		float h1 = 2 * s3 - 3 * s2 + 1;
		float h2 = -2 * s3 + 3 * s2;
		float h3 = s3 - 2 * s2 + s;
		float h4 = s3 - s2;

		return new Vector3(
			h1 * p1.x + h2 * p2.x + h3 * t1.x + h4 * t2.x,
			h1 * p1.y + h2 * p2.y + h3 * t1.y + h4 * t2.y,
			h1 * p1.z + h2 * p2.z + h3 * t1.z + h4 * t2.z
		);
	}

	/**
	 * Given two points that define the direction the track is currently headed in and the percent along the track we are,
	 * returns a Quaternion of our rotation at that point.
	 */
	protected Quaternion GetRotation(Vector3 point1, Vector3 point2, float percent) {
		//first, get a rotation that turns toward our main direction
		var tangent = point2 - point1;
		//if (point1.x != point1.x) Debug.Break();//NaN

		var ret = Quaternion.LookRotation(tangent, Vector3.up);

		//fixme: this doesn't work right yet for cases where abs(pitch) >= 90deg (gimbal lock)

		//then lerp in the roll
		float targetRoll = end.rotation.eulerAngles.z;
		if (targetRoll > 180) {
			targetRoll -= 360;
		}

		ret = ret * Quaternion.AngleAxis(targetRoll * percent, Vector3.forward);

		return ret;
	}


	/**
	 * Returns the position and location of the track "percent" percent of the distance into the
	 * track (relative to the track itself).
	 */
	public SimpleTransform GetPointAt(float percent) {
		const float delta = .001f;
		switch (curveType) {			
			case CurveType.Hermite:
			default: {
					//Hermite spline:
					Quaternion endRotation = end.rotation;

					float power = end.position.magnitude;//tangent strength
					//		float endYaw, endPitch, endRoll;
					//endTransform.getBasis().getEulerYPR(endYaw, endPitch, endRoll);
					//		float endRoll = endRotation.;
					//todo: fix the roll, which happens in track-relative space instead of world space

					Vector3 p1 = new Vector3(0, 0, 0);
					Vector3 t1 = new Vector3(0, 0, power * startStrength);
					Vector3 p2 = end.position;
					Vector3 t2 = endRotation * new Vector3(0, 0, power * endStrength);

					var pos = CalcHermite(p1, t1, p2, t2, percent);
					var dPos = CalcHermite(p1, t1, p2, t2, percent + delta);

					return new SimpleTransform(
						pos,
						GetRotation(pos, dPos, percent)
					);
				}
		}
	}

	/**
	 * Returns where on the line segment from {a}->{b} {point} is nearest to.
	 * 0 indicates the {point} is nearest {a}, 1 indicates it is nearest {b}, a value between indicates 
	 * what percentage from {a} to {b} is nearest.
	 */
	public static float NearestPartOfSegment(Vector3 point, Vector3 a, Vector3 b) {
		var lineDirection = b - a;
		var relPoint = point - a;

		var projected = Vector3.Project(relPoint, lineDirection);

		if (Vector3.Dot(projected, lineDirection) <= 0) {
			//projected vector in the opposite direction of a->b, nearest is a
			return 0;
		} else if (projected.sqrMagnitude > lineDirection.sqrMagnitude) {
			//projected vector is beyond point b, b is nearest point
			return 1;
		} else {
			//nearest point is along the line, calculate distance
			return projected.magnitude / lineDirection.magnitude;
		}
	}
	
	/** 
	 * Returns the distance from the given point to the nearest point on the line segment defined as a->b.
	 */
	public static float DistanceFromSegment(Vector3 point, Vector3 a, Vector3 b) {
		return Vector3.Distance(
			Vector3.Lerp(a, b, NearestPartOfSegment(point, a, b)),
			point
		);
		//var lineDirection = b - a;
		//var relPoint = point - a;

		//var projected = Vector3.Project(relPoint, lineDirection);

		//if (Vector3.Dot(projected, lineDirection) <= 0) {
		//	//projected vector in the opposite direction of a->b, distance is distance to a
		//	return Vector3.Distance(a, point);
		//} else if (projected.sqrMagnitude > lineDirection.sqrMagnitude) {
		//	//projected vector is beyond point b, b is nearest point
		//	return Vector3.Distance(b, point);
		//} else {
		//	//nearest point is along the line, calculate distance from projected
		//	return Vector3.Distance(projected, relPoint);
		//}

	}

	#endregion

}

}
